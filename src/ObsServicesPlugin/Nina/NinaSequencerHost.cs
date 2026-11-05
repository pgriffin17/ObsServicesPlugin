using System.Diagnostics;
using System.Reflection;
using System.Windows;
using NINA.Astrometry;
using NINA.Core.Enum;
using NINA.Core.Utility;
using NINA.Profile.Interfaces;
using NINA.Sequencer;
using NINA.Sequencer.Container;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.SequenceItem;
using NINA.Sequencer.Serialization;
using NINA.Sequencer.Utility;
using NINA.ViewModel.Sequencer;
using NINA.WPF.Base.Interfaces.Mediator;
using ObsServices.Core.Devices;
using ObsServices.Core.Protocol;

namespace ObsServicesPlugin.Nina;

/// <summary>
/// N.I.N.A.'s advanced sequencer as the core's sequencer host. Runs the three N.I.N.A. program
/// formats the service knows: <c>nina.sequence</c> (a whole sequence: replaces what is loaded and
/// starts it), <c>nina.target</c> (one target container: injected into the target area, started if
/// idle) and <c>nina.template</c> (any other container: wrapped in a target container with the
/// task's coordinates, then injected). Frames are counted from the image save pipeline.
/// </summary>
public sealed class NinaSequencerHost : ISequencerHost {
    public const string SequenceFormat = "nina.sequence";
    public const string TargetFormat = "nina.target";
    public const string TemplateFormat = "nina.template";

    /// <summary>The sequence mediator keeps its navigation view model in this private field (3.2 and 3.3 alike).</summary>
    public const string NavigationField = "sequenceNavigation";

    private readonly ISequenceMediator sequencer;
    private readonly IImageSaveMediator imageSave;
    private readonly IProfileService profiles;
    private readonly object gate = new();
    private string? runningProgram;

    public NinaSequencerHost(ISequenceMediator sequencer, IImageSaveMediator imageSave, IProfileService profiles) {
        this.sequencer = sequencer;
        this.imageSave = imageSave;
        this.profiles = profiles;
    }

    public IReadOnlyList<string> Formats { get; } = new[] { SequenceFormat, TargetFormat, TemplateFormat };

    /// <summary>Allow a <c>nina.sequence</c> program to replace the sequence the user has loaded. Off by default.</summary>
    public bool AllowWholeSequences { get; set; }

    // ---- reading -----------------------------------------------------------------------------------

    public SequencerReading Read() {
        if (!sequencer.Initialized) {
            return SequencerReading.Disconnected;
        }

        bool running;
        string? runningItem = null;
        string? program;

        try {
            running = sequencer.IsAdvancedSequenceRunning();
            var items = running ? sequencer.GetAdvancedSequencerCurrentRunningItems() : Array.Empty<ISequenceItem>();
            runningItem = items.Count == 0 ? null : string.Join(" | ", items.Select(i => i.Name).Where(n => !string.IsNullOrWhiteSpace(n)));

            lock (gate) {
                program = runningProgram;
            }

            program ??= items.Select(i => ItemUtility.GetRootContainer(i.Parent)?.SequenceTitle).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t));
        } catch (Exception e) {
            Logger.Debug($"[ObsServices] sequencer state unavailable: {e.Message}");
            return SequencerReading.Disconnected;
        }

        return new SequencerReading(true, "N.I.N.A.", NinaVersion(), profiles.ActiveProfile?.Name, running, program, runningItem, InstalledExtensions());
    }

    private static string NinaVersion() {
        try {
            var location = typeof(ISequenceMediator).Assembly.Location;
            return string.IsNullOrEmpty(location) ? string.Empty : FileVersionInfo.GetVersionInfo(location).FileVersion ?? string.Empty;
        } catch (Exception) {
            return string.Empty;
        }
    }

    /// <summary>Plugin assemblies loaded from N.I.N.A.'s plugins folder, by simple name: what a program digest's <c>extensions</c> are checked against.</summary>
    public static IReadOnlyList<string> InstalledExtensions() {
        try {
            var root = NINA.Plugin.Constants.BaseUserExtensionsFolder;
            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location) && a.Location.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                .Select(a => a.GetName().Name ?? string.Empty)
                .Where(n => n.Length > 0)
                .Distinct()
                .OrderBy(n => n, StringComparer.Ordinal)
                .ToList();
        } catch (Exception) {
            return Array.Empty<string>();
        }
    }

    // ---- deserialisation ---------------------------------------------------------------------------

    /// <summary>
    /// The factory that rebuilds entities from JSON lives on the sequencer view model, which the
    /// mediator does not expose. One private field read gets there; a rename fails loudly here and
    /// in the plugin tests rather than silently in the night.
    /// </summary>
    public static bool TryGetFactory(ISequenceMediator mediator, out ISequencerFactory? factory, out string? reason) {
        factory = null;
        var field = mediator.GetType().GetField(NavigationField, BindingFlags.Instance | BindingFlags.NonPublic);

        if (field == null) {
            reason = $"{mediator.GetType().Name} has no `{NavigationField}` field; this N.I.N.A. build moved it";
            return false;
        }

        if (field.GetValue(mediator) is not ISequenceNavigationVM navigation) {
            reason = "the sequencer view has not been registered yet";
            return false;
        }

        factory = navigation.Sequence2VM?.SequencerFactory;
        reason = factory == null ? "the advanced sequencer has no factory yet" : null;
        return factory != null;
    }

    /// <summary>Which program format a body is, from its root <c>$type</c>, or null when it is not a N.I.N.A. container.</summary>
    public static string? FormatOf(string body) {
        try {
            var root = Newtonsoft.Json.Linq.JObject.Parse(body);
            var type = root["$type"]?.ToString() ?? string.Empty;
            var name = type.Split(',')[0].Trim();

            if (name == "NINA.Sequencer.Container.SequenceRootContainer") {
                return SequenceFormat;
            }

            if (name.EndsWith("DeepSkyObjectContainer", StringComparison.Ordinal)) {
                return TargetFormat;
            }

            return name.StartsWith("NINA.Sequencer.Container.", StringComparison.Ordinal) ? TemplateFormat : null;
        } catch (Exception) {
            return null;
        }
    }

    /// <summary>Every item in a container tree that N.I.N.A. could not materialise (a plugin the receiving machine lacks).</summary>
    public static IReadOnlyList<string> UnknownItems(ISequenceContainer container) {
        var found = new List<string>();
        Walk(container);
        return found;

        void Walk(ISequenceContainer c) {
            foreach (var item in c.GetItemsSnapshot()) {
                if (item is UnknownSequenceItem unknown) {
                    found.Add(unknown.Name ?? unknown.GetType().Name);
                } else if (item is ISequenceContainer inner) {
                    Walk(inner);
                }
            }
        }
    }

    // ---- running -----------------------------------------------------------------------------------

    public async Task RunProgramAsync(ProgramRef program, ObservationTask task, IProgress<int> progress, CancellationToken cancellationToken) {
        if (!sequencer.Initialized) {
            throw new InvalidOperationException("N.I.N.A.'s sequencer is not ready yet");
        }

        if (string.IsNullOrWhiteSpace(program.Body)) {
            throw new InvalidOperationException($"program {program.Id} has no body");
        }

        var actual = FormatOf(program.Body);

        if (actual != program.Format) {
            throw new InvalidOperationException($"program {program.Id} is declared {program.Format} but its root is a {actual ?? "non-N.I.N.A. document"}");
        }

        if (program.Format == SequenceFormat && !AllowWholeSequences) {
            throw new InvalidOperationException("whole-sequence programs (nina.sequence) replace the loaded sequence; enable them in the plugin options first");
        }

        if (!TryGetFactory(sequencer, out var factory, out var reason)) {
            throw new InvalidOperationException($"cannot deserialise programs: {reason}");
        }

        var container = await OnUiThreadAsync(() => new SequenceJsonConverter(factory!).Deserialize(program.Body)).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"program {program.Id} did not deserialise into a sequence container");
        var unknown = UnknownItems(container);

        if (unknown.Count > 0) {
            Logger.Warning($"[ObsServices] program {program.Id}: {unknown.Count} item(s) this N.I.N.A. cannot run ({string.Join(", ", unknown.Distinct())}); they are skipped");
        }

        var counter = new FrameCounter(imageSave, task.Target.Name, progress);

        lock (gate) {
            runningProgram = program.Id;
        }

        try {
            switch (container) {
                case ISequenceRootContainer root:
                    await RunWholeSequenceAsync(root, cancellationToken).ConfigureAwait(false);
                    break;

                case IDeepSkyObjectContainer target:
                    ApplyTarget(target, task);
                    await RunTargetAsync(target, cancellationToken).ConfigureAwait(false);
                    break;

                default:
                    var wrapper = await OnUiThreadAsync(() => {
                        var dso = factory!.GetContainer<DeepSkyObjectContainer>();
                        dso.Name = task.Name;
                        ApplyTarget(dso, task);
                        dso.Add(container);
                        return (IDeepSkyObjectContainer)dso;
                    }).ConfigureAwait(false);
                    await RunTargetAsync(wrapper, cancellationToken).ConfigureAwait(false);
                    break;
            }
        } finally {
            counter.Dispose();

            lock (gate) {
                runningProgram = null;
            }
        }
    }

    private void ApplyTarget(IDeepSkyObjectContainer container, ObservationTask task) {
        if (string.IsNullOrWhiteSpace(task.Target.Name)) {
            return;
        }

        var settings = profiles.ActiveProfile.AstrometrySettings;
        container.Target ??= new InputTarget(Angle.ByDegree(settings.Latitude), Angle.ByDegree(settings.Longitude), settings.Horizon);
        container.Target.TargetName = task.Target.Name;
        container.Target.InputCoordinates.Coordinates = new Coordinates(Angle.ByDegree(task.Target.RaDeg), Angle.ByDegree(task.Target.DecDeg), Epoch.J2000);
    }

    private async Task RunWholeSequenceAsync(ISequenceRootContainer root, CancellationToken cancellationToken) {
        if (sequencer.IsAdvancedSequenceRunning()) {
            throw new InvalidOperationException("a sequence is already running in N.I.N.A.; stop it before a whole-sequence program can load");
        }

        await OnUiThreadAsync(() => { sequencer.SetAdvancedSequence(root); return 0; }).ConfigureAwait(false);
        using var registration = cancellationToken.Register(() => SafeCancel());
        await sequencer.StartAdvancedSequence(skipValidation: true).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
    }

    private async Task RunTargetAsync(IDeepSkyObjectContainer target, CancellationToken cancellationToken) {
        var wasRunning = sequencer.IsAdvancedSequenceRunning();
        await OnUiThreadAsync(() => { sequencer.AddAdvancedTarget(target); return 0; }).ConfigureAwait(false);
        using var registration = cancellationToken.Register(() => {
            try {
                target.Interrupt();

                foreach (var item in target.GetItemsSnapshot().Where(i => i.Status == SequenceEntityStatus.CREATED)) {
                    item.Skip();
                }
            } catch (Exception e) {
                Logger.Warning($"[ObsServices] could not interrupt the injected target: {e.Message}");
            }
        });

        if (!wasRunning) {
            // the whole sequence runs (start area, our target, end area); it returns when it is over
            await sequencer.StartAdvancedSequence(skipValidation: true).ConfigureAwait(false);
        } else {
            // appended to a running sequence: wait for our container to reach a terminal state
            while (target.Status is SequenceEntityStatus.CREATED or SequenceEntityStatus.RUNNING) {
                if (!sequencer.IsAdvancedSequenceRunning() && target.Status == SequenceEntityStatus.CREATED) {
                    throw new InvalidOperationException("the sequence stopped before this target was reached");
                }

                await Task.Delay(500, CancellationToken.None).ConfigureAwait(false);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();

        if (target.Status == SequenceEntityStatus.FAILED) {
            throw new InvalidOperationException($"target `{target.Name}` failed in N.I.N.A.'s sequencer");
        }

        if (target.Status == SequenceEntityStatus.SKIPPED && !cancellationToken.IsCancellationRequested) {
            throw new InvalidOperationException($"target `{target.Name}` was skipped by N.I.N.A.'s sequencer");
        }
    }

    private void SafeCancel() {
        try {
            sequencer.CancelAdvancedSequence();
        } catch (Exception e) {
            Logger.Warning($"[ObsServices] could not cancel the sequence: {e.Message}");
        }
    }

    private static Task<T> OnUiThreadAsync<T>(Func<T> action) {
        var dispatcher = Application.Current?.Dispatcher;

        if (dispatcher == null || dispatcher.CheckAccess()) {
            return Task.FromResult(action());
        }

        return dispatcher.InvokeAsync(action).Task;
    }

    /// <summary>Counts light frames saved for the task's target while a program runs.</summary>
    private sealed class FrameCounter : IDisposable {
        private readonly IImageSaveMediator imageSave;
        private readonly string? targetName;
        private readonly IProgress<int> progress;
        private int count;

        public FrameCounter(IImageSaveMediator imageSave, string? targetName, IProgress<int> progress) {
            this.imageSave = imageSave;
            this.targetName = string.IsNullOrWhiteSpace(targetName) ? null : targetName;
            this.progress = progress;
            imageSave.ImageSaved += OnSaved;
        }

        private void OnSaved(object? sender, ImageSavedEventArgs e) {
            var type = e.MetaData?.Image?.ImageType;

            if (!string.Equals(type, "LIGHT", StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            if (targetName != null && !string.Equals(e.MetaData?.Target?.Name, targetName, StringComparison.OrdinalIgnoreCase)) {
                return;
            }

            progress.Report(Interlocked.Increment(ref count));
        }

        public void Dispose() => imageSave.ImageSaved -= OnSaved;
    }
}
