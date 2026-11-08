using ObsServices.Core;
using ObsServices.Core.Devices;
using ObsServices.Core.Imaging;
using ObsServices.Core.Protocol;

namespace ObsServices.Core.Tests.Fakes;

/// <summary>
/// Software devices with the timing of real ones scaled down: a mount that slews at a rate and
/// tracks, a camera that takes the requested seconds times <see cref="TimeScale"/>, a roof, a
/// wheel and a focuser. The conformance reference for <see cref="Agent.ObservatoryAgent"/> on Linux.
/// </summary>
public sealed class FakeClock {
    /// <summary>Wall seconds per simulated second. 0.01 makes a 10 s exposure take 100 ms.</summary>
    public double TimeScale { get; set; } = 0.01;

    public Task SleepAsync(double simulatedSeconds, CancellationToken ct) => Task.Delay(TimeSpan.FromSeconds(simulatedSeconds * TimeScale), ct);
}

public sealed class FakeMount : IMountDevice {
    private readonly FakeClock clock;
    private readonly double lonDeg;
    private readonly double latDeg;
    private readonly object gate = new();
    private MountState state = MountState.Parked;
    private double ha;
    private double dec;
    private (double Ra, double Dec)? target;

    public FakeMount(FakeClock clock, double latDeg, double lonDeg) {
        this.clock = clock;
        this.latDeg = latDeg;
        this.lonDeg = lonDeg;
    }

    public bool Connected { get; set; } = true;

    public double SlewDegPerSecond { get; set; } = 4.0;

    /// <summary>What the driver reports; East (the normal pose) unless a test says otherwise.</summary>
    public PierSide PierSide { get; set; } = PierSide.East;

    public List<string> Calls { get; } = new();

    private double Lst => Astro.LstDeg(Protocol.Timestamp.Now(), lonDeg);

    public MountReading Read() {
        lock (gate) {
            if (!Connected) {
                return MountReading.Disconnected;
            }

            var lst = Lst;

            if (state == MountState.Tracking && target is { } t) {
                ha = Astro.HourAngleDeg(lst, t.Ra);
                dec = t.Dec;
            }

            var ra = Astro.RaDeg(lst, ha);
            var (alt, az) = Astro.AltAz(ha, dec, latDeg);
            return new MountReading(true, state, lst, ra, dec, ra, dec, alt, az, PierSide);
        }
    }

    public async Task SlewAsync(double raJ2000Deg, double decJ2000Deg, CancellationToken ct) {
        double distance;

        lock (gate) {
            Calls.Add($"slew {raJ2000Deg:0.##} {decJ2000Deg:0.##}");

            if (state == MountState.Parked) {
                throw new InvalidOperationException("mount is parked");
            }

            var targetHa = Astro.HourAngleDeg(Lst, raJ2000Deg);
            distance = Math.Max(Math.Abs(targetHa - ha), Math.Abs(decJ2000Deg - dec));
            state = MountState.Slewing;
            target = (raJ2000Deg, decJ2000Deg);
        }

        try {
            await clock.SleepAsync(distance / SlewDegPerSecond, ct);
        } catch (OperationCanceledException) {
            lock (gate) state = MountState.Stopped;
            throw;
        }

        lock (gate) {
            ha = Astro.HourAngleDeg(Lst, raJ2000Deg);
            dec = decJ2000Deg;
            state = MountState.Tracking;
        }
    }

    public Task SetTrackingAsync(bool enabled, CancellationToken ct) {
        lock (gate) {
            Calls.Add($"tracking {enabled}");

            if (state != MountState.Parked) {
                state = enabled ? MountState.Tracking : MountState.Stopped;
            }
        }

        return Task.CompletedTask;
    }

    public async Task ParkAsync(CancellationToken ct) {
        lock (gate) {
            Calls.Add("park");
            state = MountState.Slewing;
            target = null;
        }

        await clock.SleepAsync(5, ct);

        lock (gate) {
            ha = 90;
            dec = latDeg >= 0 ? 90 : -90;
            state = MountState.Parked;
        }
    }

    public Task UnparkAsync(CancellationToken ct) {
        lock (gate) {
            Calls.Add("unpark");
            state = MountState.Stopped;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) {
        lock (gate) {
            Calls.Add("stop");

            if (state != MountState.Parked) {
                state = MountState.Stopped;
                target = null;
            }
        }

        return Task.CompletedTask;
    }
}

public sealed class FakeCamera : ICameraDevice {
    private readonly FakeClock clock;
    private readonly object gate = new();
    private CameraActivity activity = CameraActivity.Idle;
    private double exposure;
    private DateTime exposureEnd;

    public FakeCamera(FakeClock clock) {
        this.clock = clock;
    }

    public bool Connected { get; set; } = true;

    public double ReadoutSeconds { get; set; } = 2.0;

    public List<ExposureRequest> Exposures { get; } = new();

    /// <summary>Sensor size of the synthetic frames; small so a test moves little.</summary>
    public int Width { get; set; } = 64;

    public int Height { get; set; } = 48;

    /// <summary>Every frame taken, as the FITS bytes the sink was handed.</summary>
    public List<(string Hash, byte[] Fits, FrameInfo Info)> Frames { get; } = new();

    /// <summary>Where frames go once read out (the agent's announce); null discards them.</summary>
    public Func<ReadOnlyMemory<ushort>, int, int, byte[], FrameInfo, Task>? FrameTaken { get; set; }

    /// <summary>A deterministic sky: a pedestal with noise and a few Gaussian stars, seeded by the frame count.</summary>
    public static ushort[] Synthesize(int width, int height, int seed) {
        var rng = new Random(seed);
        var px = new double[width * height];

        for (var i = 0; i < px.Length; i++) {
            px[i] = 500 + 8 * Gauss(rng);
        }

        for (var s = 0; s < 12; s++) {
            double cx = rng.NextDouble() * width, cy = rng.NextDouble() * height, amp = 200 + rng.NextDouble() * 20000, sig = 1.2 + rng.NextDouble() * 1.3;
            var r = (int)Math.Ceiling(4 * sig);

            for (var y = Math.Max(0, (int)cy - r); y < Math.Min(height, (int)cy + r + 1); y++) {
                for (var x = Math.Max(0, (int)cx - r); x < Math.Min(width, (int)cx + r + 1); x++) {
                    var d2 = Math.Pow(x + 0.5 - cx, 2) + Math.Pow(y + 0.5 - cy, 2);
                    px[y * width + x] += amp * Math.Exp(-d2 / (2 * sig * sig));
                }
            }
        }

        return px.Select(v => (ushort)Math.Clamp(Math.Round(v), 0, 65535)).ToArray();
    }

    private static double Gauss(Random rng) => Math.Sqrt(-2.0 * Math.Log(Math.Max(rng.NextDouble(), 1e-12))) * Math.Cos(2 * Math.PI * rng.NextDouble());

    public CameraReading Read() {
        lock (gate) {
            if (!Connected) {
                return CameraReading.Disconnected;
            }

            var remaining = activity == CameraActivity.Exposing ? Math.Max(0, (exposureEnd - DateTime.UtcNow).TotalSeconds / clock.TimeScale) : 0;
            return new CameraReading(true, activity, -10.0, activity == CameraActivity.Exposing ? exposure : 0, remaining);
        }
    }

    public async Task ExposeAsync(ExposureRequest request, CancellationToken ct) {
        lock (gate) {
            activity = CameraActivity.Exposing;
            exposure = request.Seconds;
            exposureEnd = DateTime.UtcNow + TimeSpan.FromSeconds(request.Seconds * clock.TimeScale);
        }

        var started = Timestamp.Now();

        try {
            await clock.SleepAsync(request.Seconds, ct);
            lock (gate) activity = CameraActivity.ReadingOut;
            await clock.SleepAsync(ReadoutSeconds, ct);
            int seed;

            lock (gate) {
                Exposures.Add(request);
                seed = Exposures.Count;
            }

            if (FrameTaken is { } sink) {
                var pixels = Synthesize(Width, Height, seed);
                var fits = Fits.Write(pixels, Width, Height, new[] {
                    new FitsCard("IMAGETYP", "LIGHT", "frame type"),
                    new FitsCard("EXPTIME", request.Seconds, "seconds"),
                    new FitsCard("FILTER", request.Filter),
                    new FitsCard("OBJECT", request.TargetName),
                    new FitsCard("DATE-OBS", DateTimeOffset.FromUnixTimeMilliseconds((long)(started * 1000)).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff")),
                    new FitsCard("GAIN", 100),
                    new FitsCard("OFFSET", 50),
                    new FitsCard("XBINNING", 1),
                    new FitsCard("CCD-TEMP", -10.0),
                });
                var info = new FrameInfo {
                    Kind = ImageKind.Light,
                    CapturedAt = started,
                    ExposureS = request.Seconds,
                    Filter = request.Filter,
                    TaskId = request.TaskId,
                    Gain = 100,
                    Offset = 50,
                    Binning = 1,
                    TemperatureC = -10.0,
                    FileName = $"{request.TargetName}_{request.Filter}_{request.FrameIndex + 1:000}.fits",
                };
                lock (gate) Frames.Add((Fits.HashOfUInt16(pixels), fits, info));
                await sink(pixels, Width, Height, fits, info);
            }
        } finally {
            lock (gate) activity = CameraActivity.Idle;
        }
    }
}

public sealed class FakeFilterWheel : IFilterWheelDevice {
    private readonly FakeClock clock;

    public FakeFilterWheel(FakeClock clock, params string[] filters) {
        this.clock = clock;
        Filters = filters;
        CurrentFilter = filters.FirstOrDefault();
    }

    public bool Connected { get; set; } = true;

    public IReadOnlyList<string> Filters { get; }

    public string? CurrentFilter { get; private set; }

    public List<string> Changes { get; } = new();

    public async Task ChangeFilterAsync(string filter, CancellationToken ct) {
        if (!Filters.Contains(filter)) {
            throw new ArgumentException($"no filter {filter}");
        }

        await clock.SleepAsync(3, ct);
        CurrentFilter = filter;
        Changes.Add(filter);
    }
}

public sealed class FakeRoof : IRoofDevice {
    private readonly FakeClock clock;
    private RoofActivity activity = RoofActivity.Closed;

    public FakeRoof(FakeClock clock) {
        this.clock = clock;
    }

    public bool Connected { get; set; } = true;

    public double TravelSeconds { get; set; } = 20;

    public RoofActivity Read() => activity;

    public async Task OpenAsync(CancellationToken ct) {
        if (activity == RoofActivity.Open) {
            return;
        }

        activity = RoofActivity.Opening;
        await clock.SleepAsync(TravelSeconds, ct);
        activity = RoofActivity.Open;
    }

    public async Task CloseAsync(CancellationToken ct) {
        if (activity == RoofActivity.Closed) {
            return;
        }

        activity = RoofActivity.Closing;
        await clock.SleepAsync(TravelSeconds, ct);
        activity = RoofActivity.Closed;
    }
}

public sealed class FakeFocuser : IFocuserDevice {
    public bool Connected { get; set; } = true;

    public int? Position { get; set; } = 15000;

    public Task MoveAsync(int position, CancellationToken cancellationToken) {
        Position = position;
        return Task.CompletedTask;
    }
}

/// <summary>
/// A sequencer in software: "runs" a program by sleeping through the digest's exposures in scaled
/// time, one frame at a time, and remembers the bodies it was handed.
/// </summary>
public sealed class FakeSequencerHost : ISequencerHost {
    private readonly FakeClock clock;
    private bool running;
    private string? program;
    private string? runningItem;

    public FakeSequencerHost(FakeClock clock, params string[] formats) {
        this.clock = clock;
        Formats = formats.Length == 0 ? new[] { "nina.sequence", "nina.target", "nina.template" } : formats;
    }

    public bool Connected { get; set; } = true;

    public IReadOnlyList<string> Formats { get; }

    public List<string> Extensions { get; } = new() { "DaleGhent.NINA.AstroPhysicsTools" };

    public List<(string Id, long Revision, string Sha256, int BodyLength)> Runs { get; } = new();

    public SequencerReading Read() => Connected
        ? new SequencerReading(true, "N.I.N.A.", "3.3.0.1060", "Test profile", running, program, runningItem, Extensions)
        : SequencerReading.Disconnected;

    public async Task RunProgramAsync(ObsServices.Core.Protocol.ProgramRef p, ObsServices.Core.Protocol.ObservationTask task, IProgress<int> progress, CancellationToken cancellationToken) {
        Runs.Add((p.Id, p.Revision, p.Sha256, p.Body?.Length ?? 0));
        running = true;
        program = p.Id;

        try {
            var frames = task.Sequence.SelectMany(e => Enumerable.Repeat(e, e.Count)).ToList();
            var done = 0;

            foreach (var frame in frames) {
                cancellationToken.ThrowIfCancellationRequested();
                runningItem = $"Take Exposure {frame.Filter} {frame.Seconds:0}s";
                await clock.SleepAsync(frame.Seconds, cancellationToken);
                progress.Report(++done);
            }
        } finally {
            running = false;
            runningItem = null;
        }
    }
}
