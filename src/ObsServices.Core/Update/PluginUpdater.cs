using System.Diagnostics;

namespace ObsServices.Core.Update;

/// <summary>Where the source comes from, what to build and where the result goes.</summary>
public sealed class UpdateOptions {
    public required string RepositoryUrl { get; init; }

    public required string Branch { get; init; }

    /// <summary>The checkout to build: the plugin's own clone when <see cref="Managed"/>, otherwise the user's.</summary>
    public required string SourceDirectory { get; init; }

    /// <summary>True when the plugin owns <see cref="SourceDirectory"/> and may clone into it and reset it at will.</summary>
    public bool Managed { get; init; } = true;

    /// <summary>The folder the host installs from at its next start.</summary>
    public required string StageDirectory { get; init; }

    public string ProjectPath { get; init; } = Path.Combine("src", "ObsServicesPlugin", "ObsServicesPlugin.csproj");

    public string Configuration { get; init; } = "Release";

    public string Git { get; init; } = "git";

    public string Dotnet { get; init; } = "dotnet";

    public TimeSpan BuildTimeout { get; init; } = TimeSpan.FromMinutes(20);
}

/// <summary>What a check found: the checkout's commit, the branch tip, and the distance.</summary>
public sealed record UpdateCheck(Commit Local, Commit Remote, int Behind) {
    public bool UpToDate => Behind == 0 && Local.Sha == Remote.Sha;
}

/// <summary>A build that now waits in the stage folder.</summary>
public sealed record StagedUpdate(Commit Commit, IReadOnlyList<string> Files, TimeSpan BuildTime);

/// <summary>
/// Sync a branch, build the plugin and stage the package for the host to install at its next start. Everything
/// runs out of process (git, dotnet) and reports through <see cref="Log"/>; nothing here touches the files the
/// host has loaded, so it is safe while the plugin itself is running.
/// </summary>
public sealed class PluginUpdater {
    public PluginUpdater(UpdateOptions options) {
        Options = options;
    }

    public UpdateOptions Options { get; }

    /// <summary>Every line the tools print, plus the updater's own progress notes.</summary>
    public event Action<string>? Log;

    private void Note(string line) => Log?.Invoke(line);

    /// <summary>The checkout, cloned first when the plugin owns the folder and it is empty.</summary>
    public async Task<GitRepository> OpenAsync(CancellationToken ct = default) {
        var dir = Options.SourceDirectory;

        if (!GitRepository.IsRepository(dir)) {
            if (!Options.Managed) {
                throw new CommandException($"{dir} is not a git checkout");
            }

            if (Directory.Exists(dir) && Directory.EnumerateFileSystemEntries(dir).Any()) {
                throw new CommandException($"{dir} exists and is not empty; the plugin will not clone over it");
            }

            Note($"cloning {Options.RepositoryUrl} ({Options.Branch}) into {dir}");
            return await GitRepository.CloneAsync(Options.RepositoryUrl, Options.Branch, dir, Note, Options.Git, ct);
        }

        var repo = new GitRepository(dir, Note) { Git = Options.Git };

        if (Options.Managed) {
            var url = await repo.RemoteUrlAsync(ct);

            if (!string.Equals(url, Options.RepositoryUrl, StringComparison.OrdinalIgnoreCase)) {
                Note($"pointing the clone at {Options.RepositoryUrl}");
                await repo.SetRemoteUrlAsync(Options.RepositoryUrl, ct);
            }
        }

        return repo;
    }

    /// <summary>Fetches and compares; changes nothing in the working tree.</summary>
    public async Task<UpdateCheck> CheckAsync(CancellationToken ct = default) {
        var repo = await OpenAsync(ct);
        await repo.FetchAsync(Options.Branch, ct);
        var local = await repo.HeadAsync(ct);
        var remote = await repo.RemoteHeadAsync(Options.Branch, ct);
        var behind = await repo.BehindAsync(Options.Branch, ct);
        return new UpdateCheck(local, remote, behind);
    }

    /// <summary>Sync, build, stage. Throws <see cref="CommandException"/> with the tool's last lines when a step fails.</summary>
    public async Task<StagedUpdate> UpdateAsync(CancellationToken ct = default) {
        var repo = await OpenAsync(ct);
        Note($"syncing {Options.Branch}");
        var commit = await repo.SyncAsync(Options.Branch, Options.Managed, ct);
        Note($"at {commit}");

        var project = Path.Combine(Options.SourceDirectory, Options.ProjectPath);

        if (!File.Exists(project)) {
            throw new CommandException($"{Options.ProjectPath} is not in {Options.Branch}; is this the right repository?");
        }

        ClearStage();
        Note($"building {Options.Configuration} into {Options.StageDirectory}");
        var watch = Stopwatch.StartNew();
        await CommandRunner.RequireAsync(Options.Dotnet, BuildArguments(project), Options.SourceDirectory, Note, Options.BuildTimeout, ct);
        watch.Stop();

        var files = StaleFiles.ReadManifest(Options.StageDirectory);

        if (files == null || files.Count == 0) {
            throw new CommandException($"the build wrote no package manifest to {Options.StageDirectory}; does {Options.Branch} have the StageForNina target?");
        }

        var missing = files.Where(f => !File.Exists(Path.Combine(Options.StageDirectory, f))).ToList();

        if (missing.Count > 0) {
            throw new CommandException($"staged package is incomplete: {string.Join(", ", missing)}");
        }

        Note($"staged {files.Count} files in {watch.Elapsed.TotalSeconds:0}s");
        return new StagedUpdate(commit, files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList(), watch.Elapsed);
    }

    /// <summary>The dotnet build line: Release, the staging target, the stage folder, quiet output.</summary>
    public IReadOnlyList<string> BuildArguments(string project) => new[] {
        "build", project,
        "-c", Options.Configuration,
        "-t:StageForNina",
        $"-p:NinaStageDirectory={Options.StageDirectory}",
        "-nologo", "-v:minimal", "-clp:NoSummary",
    };

    /// <summary>True when a staged package is waiting for the host to restart.</summary>
    public bool HasStagedPackage() => StaleFiles.ReadManifest(Options.StageDirectory) is { Count: > 0 };

    /// <summary>Removes a previous stage, but only one this updater wrote (the manifest proves it).</summary>
    public void ClearStage() {
        var dir = Options.StageDirectory;

        if (!Directory.Exists(dir)) {
            return;
        }

        var manifest = StaleFiles.ReadManifest(dir);

        if (manifest == null) {
            if (Directory.EnumerateFileSystemEntries(dir).Any()) {
                throw new CommandException($"{dir} holds files the plugin did not stage; clear it by hand");
            }

            return;
        }

        foreach (var f in manifest) {
            var path = Path.Combine(dir, f);

            if (File.Exists(path)) {
                File.Delete(path);
            }
        }

        File.Delete(Path.Combine(dir, StaleFiles.ManifestName));
    }
}
