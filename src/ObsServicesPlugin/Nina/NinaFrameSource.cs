using System.IO;
using NINA.Core.Utility;
using NINA.Image.FileFormat.FITS;
using NINA.Image.Interfaces;
using NINA.WPF.Base.Interfaces.Mediator;
using ObsServices.Core.Imaging;
using ObsServices.Core.Protocol;

namespace ObsServicesPlugin.Nina;

/// <summary>Which frames leave the machine. Lights when sharing is on; calibration frames and snapshots each need their own switch.</summary>
public sealed record FrameSharing(bool Enabled, bool Calibration, bool Snapshots) {
    public bool Shares(ImageKind kind) => Enabled && kind switch {
        ImageKind.Light => true,
        ImageKind.Other => Snapshots,
        _ => Calibration,
    };
}

/// <summary>
/// Frames N.I.N.A. saves, handed to the agent's spool. Hooks the image save pipeline before the file is
/// written: the raw 16-bit array and the metadata are taken there, a plain FITS copy is written with
/// N.I.N.A.'s own FITS writer (so the spool is independent of the user's XISF, TIFF or compressed output,
/// which the service cannot decode), and the frame is described and announced on a background worker, one
/// at a time, so a slow disk never holds up the next exposure. N.I.N.A.'s statistics and star detection
/// ride along when its processing finishes in time.
/// </summary>
public sealed class NinaFrameSource : IDisposable {
    private readonly IImageSaveMediator imageSave;
    private readonly Func<NinaSiteAgent?> agent;
    private readonly Func<FrameSharing> sharing;
    private readonly SemaphoreSlim worker = new(1, 1);
    private int queued;
    private bool warnedNoFlatArray;

    public NinaFrameSource(IImageSaveMediator imageSave, Func<NinaSiteAgent?> agent, Func<FrameSharing> sharing) {
        this.imageSave = imageSave;
        this.agent = agent;
        this.sharing = sharing;
        imageSave.BeforeImageSaved += OnBeforeImageSaved;
    }

    /// <summary>Frames taken from the pipeline and not yet announced.</summary>
    public int Queued => Volatile.Read(ref queued);

    /// <summary>Frames announced since the plugin loaded.</summary>
    public int Announced { get; private set; }

    /// <summary>Why the last frame could not be announced; null while all is well.</summary>
    public string? LastError { get; private set; }

    /// <summary>Raised when <see cref="Queued"/>, <see cref="Announced"/> or <see cref="LastError"/> change.</summary>
    public event Action? Changed;

    private Task OnBeforeImageSaved(object sender, BeforeImageSavedEventArgs e) {
        try {
            var share = sharing();

            if (!share.Enabled) {
                return Task.CompletedTask;
            }

            var a = agent();

            if (a?.Spool == null) {
                return Task.CompletedTask;
            }

            var data = e.Image;
            var flat = data?.Data?.FlatArray;

            if (data == null || flat == null) {
                if (!warnedNoFlatArray) {
                    warnedNoFlatArray = true;
                    Logger.Warning("[ObsServices] this camera's frames are not 16-bit arrays; they are not shared with the service yet");
                }

                return Task.CompletedTask;
            }

            var meta = data.MetaData;
            var kind = ImageKindExtensions.ParseImageKind(meta.Image.ImageType);

            if (!share.Shares(kind)) {
                return Task.CompletedTask;
            }

            var width = data.Properties.Width;
            var height = data.Properties.Height;

            if (flat.Length < width * height) {
                return Task.CompletedTask;
            }

            // the header now, while the metadata is what the pipeline is about to write
            var fits = new FITS(flat, width, height);
            fits.PopulateHeaderCards(meta);
            var start = meta.Image.ExposureStart;
            var info = new FrameInfo {
                Kind = kind,
                CapturedAt = UnixSeconds(start),
                ExposureS = double.IsFinite(meta.Image.ExposureTime) && meta.Image.ExposureTime >= 0 ? meta.Image.ExposureTime : 0,
                Filter = string.IsNullOrWhiteSpace(meta.FilterWheel.Filter) ? null : meta.FilterWheel.Filter,
                Gain = meta.Camera.Gain >= 0 ? meta.Camera.Gain : null,
                Offset = meta.Camera.Offset >= 0 ? meta.Camera.Offset : null,
                Binning = meta.Camera.BinX > 0 ? meta.Camera.BinX : null,
                TemperatureC = double.IsFinite(meta.Camera.Temperature) ? meta.Camera.Temperature : null,
                FileName = FileNameFor(meta.Target.Name, kind, start),
            };

            Interlocked.Increment(ref queued);
            Changed?.Invoke();
            _ = Task.Run(() => ProcessAsync(a, fits, flat, width, height, info, e.ImagePrepareTask));
        } catch (Exception ex) {
            Logger.Warning($"[ObsServices] frame not shared: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    private async Task ProcessAsync(NinaSiteAgent a, FITS fits, ushort[] flat, int width, int height, FrameInfo info, Task<IRenderedImage>? prepared) {
        await worker.WaitAsync();

        try {
            info.Stats = await StatsFrom(prepared);
            byte[] bytes;

            using (var ms = new MemoryStream(width * height * 2 + 8 * FITS.BLOCKSIZE)) {
                fits.Write(ms);
                bytes = ms.ToArray();
            }

            var hash = await a.AnnounceFrameAsync(flat, width, height, bytes, info);
            Announced++;
            LastError = null;
            Logger.Debug($"[ObsServices] announced frame {hash[..12]}… ({info.Kind.AsString()}, {bytes.Length} bytes)");
        } catch (Exception ex) {
            LastError = ex.Message;
            Logger.Warning($"[ObsServices] frame not shared: {ex.Message}");
        } finally {
            worker.Release();
            Interlocked.Decrement(ref queued);
            Changed?.Invoke();
        }
    }

    /// <summary>N.I.N.A.'s own measurements once its processing is through; null when it fails, and the core measures the pixels itself.</summary>
    private static async Task<ImageStats?> StatsFrom(Task<IRenderedImage>? prepared) {
        if (prepared == null) {
            return null;
        }

        try {
            var rendered = await prepared;
            var raw = rendered?.RawImageData;

            if (raw == null) {
                return null;
            }

            var statistics = await raw.Statistics;
            var stars = raw.StarDetectionAnalysis;
            return new ImageStats {
                Median = statistics.Median,
                Sigma = 1.4826 * statistics.MedianAbsoluteDeviation,
                Min = statistics.Min,
                Max = statistics.Max,
                Stars = stars is { DetectedStars: > 0 } ? stars.DetectedStars : null,
                Hfr = stars is { HFR: > 0 } && double.IsFinite(stars.HFR) ? stars.HFR : null,
            };
        } catch (Exception e) {
            Logger.Debug($"[ObsServices] N.I.N.A. statistics unavailable for this frame: {e.Message}");
            return null;
        }
    }

    /// <summary>N.I.N.A. stamps exposures with a local <see cref="DateTime"/>; the service wants unix seconds.</summary>
    public static double UnixSeconds(DateTime start) {
        if (start == DateTime.MinValue) {
            return Timestamp.Now();
        }

        var utc = start.Kind == DateTimeKind.Utc ? start : start.ToUniversalTime();
        return new DateTimeOffset(utc, TimeSpan.Zero).ToUnixTimeMilliseconds() / 1000.0;
    }

    private static string FileNameFor(string? target, ImageKind kind, DateTime start) {
        var stem = string.IsNullOrWhiteSpace(target) ? kind.AsString() : string.Concat(target.Trim().Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        var when = start == DateTime.MinValue ? DateTime.Now : start;
        return $"{stem}_{when:yyyy-MM-dd_HH-mm-ss}.fits";
    }

    public void Dispose() => imageSave.BeforeImageSaved -= OnBeforeImageSaved;
}
