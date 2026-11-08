using System.IO;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using NINA.Core.MyMessageBox;
using NINA.Core.Utility;
using NINA.Plugin;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using ObsServices.Core.Update;

namespace ObsServicesPlugin.Nina;

/// <summary>
/// The options page's "Update from branch" panel. Builds the plugin from a git branch while N.I.N.A. runs and
/// stages the package where N.I.N.A.'s own plugin manager installs from at the next start
/// (<c>%LOCALAPPDATA%\NINA\PluginStaging\&lt;version&gt;\&lt;plugin folder&gt;</c>), so the loaded assemblies are never
/// touched. The git and dotnet work is <see cref="PluginUpdater"/> in the core; this class adds the N.I.N.A. paths,
/// the restart, the stale-file sweep and the bindings.
/// </summary>
public sealed class NinaUpdater : INotifyPropertyChanged {
    private const int LogLines = 40;
    private readonly Func<Settings> settings;
    private readonly IProfileService profiles;
    private readonly ISequenceMediator? sequence;
    private readonly Queue<string> log = new();
    private CancellationTokenSource? running;
    private string status = string.Empty;
    private UpdateCheck? lastCheck;

    public NinaUpdater(Func<Settings> settings, IProfileService profiles, ISequenceMediator? sequence, Assembly plugin, string title) {
        this.settings = settings;
        this.profiles = profiles;
        this.sequence = sequence;
        Installed = InstalledBuild.Of(plugin);
        PluginDirectory = Path.GetDirectoryName(plugin.Location) ?? string.Empty;
        StageDirectory = Path.Combine(Constants.StagingFolder, PluginFolderName(PluginDirectory, Constants.UserExtensionsFolder, title));
        CheckCommand = new AsyncRelayCommand(CheckAsync, () => !Busy);
        UpdateCommand = new AsyncRelayCommand(UpdateAsync, () => !Busy);
        CancelCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(Cancel, () => Busy);
        RestartCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(Restart, () => RestartPending && !Busy);
        status = RestartPending ? "An update is staged. Restart N.I.N.A. to load it." : string.Empty;
    }

    /// <summary>The build N.I.N.A. loaded.</summary>
    public InstalledBuild Installed { get; }

    public string InstalledText => Installed.ToString();

    /// <summary>Where this build's assemblies live.</summary>
    public string PluginDirectory { get; }

    /// <summary>Where a staged build waits for N.I.N.A.'s next start.</summary>
    public string StageDirectory { get; }

    /// <summary>The checkout the next update builds: the setting, or the plugin's own clone.</summary>
    public string SourceDirectory => ResolveSourceDirectory(settings().UpdateSourceDirectory);

    public bool Managed => string.IsNullOrWhiteSpace(settings().UpdateSourceDirectory);

    public static string ResolveSourceDirectory(string setting) =>
        string.IsNullOrWhiteSpace(setting) ? Path.Combine(CoreUtil.APPLICATIONTEMPPATH, "ObsServices", "src") : Environment.ExpandEnvironmentVariables(setting);

    public ICommand CheckCommand { get; }

    public ICommand UpdateCommand { get; }

    public ICommand CancelCommand { get; }

    public ICommand RestartCommand { get; }

    public bool Busy => running != null;

    public string Status {
        get => status;
        private set => Set(ref status, value);
    }

    /// <summary>The last lines git and dotnet printed.</summary>
    public string Log {
        get {
            lock (log) return string.Join(Environment.NewLine, log);
        }
    }

    public bool RestartPending => StaleFiles.ReadManifest(StageDirectory) is { Count: > 0 };

    /// <summary>Commits the branch has that this build lacks, after a check; null before one.</summary>
    public int? Behind => lastCheck?.Behind;

    public bool UpdateAvailable => lastCheck is { Behind: > 0 } || (lastCheck != null && !Installed.Is(lastCheck.Remote));

    private PluginUpdater Make() {
        var s = settings();
        var updater = new PluginUpdater(new UpdateOptions {
            RepositoryUrl = s.UpdateRepositoryUrl,
            Branch = s.UpdateBranch,
            SourceDirectory = SourceDirectory,
            Managed = Managed,
            StageDirectory = StageDirectory,
        });
        updater.Log += Append;
        return updater;
    }

    private void Append(string line) {
        lock (log) {
            log.Enqueue(line);

            while (log.Count > LogLines) {
                log.Dequeue();
            }
        }

        Raise(nameof(Log));
    }

    private void Clear() {
        lock (log) log.Clear();
        Raise(nameof(Log));
    }

    private CancellationToken Begin(string what) {
        if (running != null) {
            throw new InvalidOperationException("an update step is already running");
        }

        running = new CancellationTokenSource();
        Clear();
        Status = what;
        RaiseBusy();
        return running.Token;
    }

    private void End() {
        running?.Dispose();
        running = null;
        RaiseBusy();
        Raise(nameof(RestartPending));
        Raise(nameof(Behind));
        Raise(nameof(UpdateAvailable));
    }

    /// <summary>Fetches the branch and compares; the working tree is not changed.</summary>
    public async Task CheckAsync() {
        var ct = Begin("Checking…");

        try {
            var check = await Task.Run(() => Make().CheckAsync(ct), ct);
            lastCheck = check;
            Logger.Info($"[ObsServices] update check: installed {Installed}, {check.Remote.Branch} at {check.Remote.Short}, checkout {check.Behind} behind");
            Status = Describe(check);
        } catch (OperationCanceledException) {
            Status = "Check cancelled.";
        } catch (Exception e) {
            Logger.Warning($"[ObsServices] update check failed: {e.Message}");
            Status = $"Check failed: {e.Message}";
        } finally {
            End();
        }
    }

    private string Describe(UpdateCheck check) {
        var tip = $"{check.Remote.Branch} is at {check.Remote.Short} ({check.Remote.When.ToLocalTime():yyyy-MM-dd HH:mm}, {check.Remote.Subject}).";

        if (Installed.Is(check.Remote)) {
            return $"{tip} This is the build N.I.N.A. is running.";
        }

        if (RestartPending) {
            return $"{tip} An update is staged; restart N.I.N.A. to load it.";
        }

        if (!Installed.Known) {
            return $"{tip} The running build has no commit stamp, so it cannot be compared; update to be sure.";
        }

        return check.Behind > 0
            ? $"{tip} The checkout is {check.Behind} commit(s) behind; the running build is {Installed.Branch}@{Installed.Short}."
            : $"{tip} The running build is {Installed.Branch}@{Installed.Short}; update to switch.";
    }

    /// <summary>Sync, build, stage. N.I.N.A. installs the result at its next start.</summary>
    public async Task UpdateAsync() {
        var ct = Begin("Updating… (a first build can take a few minutes)");

        try {
            var staged = await Task.Run(() => Make().UpdateAsync(ct), ct);
            Logger.Info($"[ObsServices] staged {staged.Commit} in {StageDirectory}");
            Status = Installed.Is(staged.Commit)
                ? $"Built {staged.Commit.Branch}@{staged.Commit.Short} in {staged.BuildTime.TotalSeconds:0}s, the same commit N.I.N.A. is running. Restart N.I.N.A. to load it anyway."
                : $"Built {staged.Commit.Branch}@{staged.Commit.Short} ({staged.Commit.Subject}) in {staged.BuildTime.TotalSeconds:0}s. Restart N.I.N.A. to load it.";
        } catch (OperationCanceledException) {
            Status = "Update cancelled.";
        } catch (Exception e) {
            Logger.Warning($"[ObsServices] update failed: {e.Message}");
            Status = $"Update failed: {e.Message}";
        } finally {
            End();
        }
    }

    public void Cancel() => running?.Cancel();

    /// <summary>
    /// What N.I.N.A.'s plugin page does after an install: release the profile, start a new N.I.N.A., shut this one
    /// down. Refused while a sequence runs; asks first.
    /// </summary>
    public void Restart() {
        if (sequence?.IsAdvancedSequenceRunning() == true) {
            Status = "A sequence is running; restart N.I.N.A. when it is done.";
            return;
        }

        var answer = MyMessageBox.Show("Restart N.I.N.A. now to load the staged update? Equipment is disconnected and reconnected as on any restart.", "Restart N.I.N.A.", MessageBoxButton.YesNo, MessageBoxResult.No);

        if (answer != MessageBoxResult.Yes) {
            return;
        }

        var exe = Environment.ProcessPath;

        if (string.IsNullOrEmpty(exe)) {
            Status = "Cannot find N.I.N.A.'s executable; restart it by hand.";
            return;
        }

        Logger.Info("[ObsServices] restarting N.I.N.A. to load a staged plugin update");
        profiles.Release();
        Process.Start(new ProcessStartInfo(exe) { UseShellExecute = false });
        Application.Current.Shutdown();
    }

    /// <summary>
    /// Moves assemblies in the plugin folder that this build's package manifest does not list into N.I.N.A.'s
    /// deletion area, which it empties at the next start. Loaded files can be moved but not deleted, which is why
    /// N.I.N.A. itself does it this way. Returns what was moved.
    /// </summary>
    public IReadOnlyList<string> RemoveStaleFiles() {
        var stale = StaleFiles.Find(PluginDirectory);

        if (stale.Count == 0) {
            return stale;
        }

        Directory.CreateDirectory(Constants.DeletionFolder);
        var moved = new List<string>();

        foreach (var file in stale) {
            try {
                File.Move(file, Path.Combine(Constants.DeletionFolder, Path.GetRandomFileName()));
                moved.Add(file);
                Logger.Info($"[ObsServices] moved stale {Path.GetFileName(file)} out of the plugin folder");
            } catch (Exception e) {
                Logger.Warning($"[ObsServices] cannot move stale {Path.GetFileName(file)}: {e.Message}");
            }
        }

        return moved;
    }

    /// <summary>
    /// The plugin's folder name under N.I.N.A.'s plugins directory: the folder this build was loaded from when it
    /// is inside that directory, else the title made safe the way Deploy-Plugin.ps1 does it.
    /// </summary>
    public static string PluginFolderName(string pluginDirectory, string userExtensionsFolder, string title) {
        if (!string.IsNullOrEmpty(pluginDirectory) && !string.IsNullOrEmpty(userExtensionsFolder)) {
            var root = Path.GetFullPath(userExtensionsFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var dir = Path.GetFullPath(pluginDirectory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            if (dir.Length > root.Length + 1 && dir.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) {
                var relative = dir[(root.Length + 1)..];

                if (!relative.Contains(Path.DirectorySeparatorChar) && !relative.Contains(Path.AltDirectorySeparatorChar)) {
                    return relative;
                }
            }
        }

        return SafeFolderName(title);
    }

    public static string SafeFolderName(string title) {
        var folder = title.Replace('\\', '-').Replace('/', '-');

        foreach (var c in Path.GetInvalidFileNameChars()) {
            folder = folder.Replace(c, '_');
        }

        if (string.IsNullOrWhiteSpace(folder) || folder is "." or "..") {
            throw new InvalidOperationException("the plugin title does not form a folder name");
        }

        return folder;
    }

    // ---- INotifyPropertyChanged --------------------------------------------------------------------

    public event PropertyChangedEventHandler? PropertyChanged;

    private void RaiseBusy() {
        Raise(nameof(Busy));
        ((IRelayCommand)CheckCommand).NotifyCanExecuteChanged();
        ((IRelayCommand)UpdateCommand).NotifyCanExecuteChanged();
        ((IRelayCommand)CancelCommand).NotifyCanExecuteChanged();
        ((IRelayCommand)RestartCommand).NotifyCanExecuteChanged();
    }

    private void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null) {
        if (!EqualityComparer<T>.Default.Equals(field, value)) {
            field = value;
            Raise(name);
        }
    }
}
