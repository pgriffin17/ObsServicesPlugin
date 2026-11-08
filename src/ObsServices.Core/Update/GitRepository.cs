using System.Globalization;

namespace ObsServices.Core.Update;

/// <summary>One commit, as the updater shows it.</summary>
public sealed record Commit(string Sha, string Branch, DateTimeOffset When, string Subject) {
    public string Short => Sha.Length > 7 ? Sha[..7] : Sha;

    public override string ToString() => $"{Branch}@{Short} ({When:yyyy-MM-dd HH:mm}) {Subject}";
}

/// <summary>
/// The git operations the updater needs, over the git executable. Two modes: a <b>managed</b> clone the
/// plugin owns and keeps equal to the remote branch (reset hard, no questions), or the user's own checkout,
/// which is only ever fast-forwarded and left alone when it has uncommitted changes.
/// </summary>
public sealed class GitRepository {
    private readonly Action<string>? log;

    public GitRepository(string directory, Action<string>? log = null) {
        Directory = directory;
        this.log = log;
    }

    public string Directory { get; }

    /// <summary>The git executable; a full path when git is not on PATH.</summary>
    public string Git { get; init; } = "git";

    public TimeSpan NetworkTimeout { get; init; } = TimeSpan.FromMinutes(5);

    public static bool IsRepository(string directory) =>
        System.IO.Directory.Exists(Path.Combine(directory, ".git")) || File.Exists(Path.Combine(directory, ".git"));

    public static async Task<GitRepository> CloneAsync(string url, string branch, string directory, Action<string>? log = null, string git = "git", CancellationToken ct = default) {
        System.IO.Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(directory))!);
        await CommandRunner.RequireAsync(git, new[] { "clone", "--branch", branch, "--single-branch", url, directory }, null, log, TimeSpan.FromMinutes(10), ct);
        return new GitRepository(directory, log) { Git = git };
    }

    private Task<CommandResult> RunAsync(CancellationToken ct, TimeSpan? timeout, params string[] args) =>
        CommandRunner.RunAsync(Git, args, Directory, log, timeout ?? TimeSpan.FromMinutes(1), ct);

    private Task<CommandResult> RequireAsync(CancellationToken ct, TimeSpan? timeout, params string[] args) =>
        CommandRunner.RequireAsync(Git, args, Directory, log, timeout ?? TimeSpan.FromMinutes(1), ct);

    public async Task<string> RemoteUrlAsync(CancellationToken ct = default) =>
        (await RunAsync(ct, null, "remote", "get-url", "origin")).Text;

    public async Task SetRemoteUrlAsync(string url, CancellationToken ct = default) =>
        await RequireAsync(ct, null, "remote", "set-url", "origin", url);

    /// <summary>Fetches one branch; a single-branch clone learns about a new branch this way too.</summary>
    public async Task FetchAsync(string branch, CancellationToken ct = default) =>
        await RequireAsync(ct, NetworkTimeout, "fetch", "--prune", "origin", $"+refs/heads/{branch}:refs/remotes/origin/{branch}");

    public async Task<string> CurrentBranchAsync(CancellationToken ct = default) =>
        (await RequireAsync(ct, null, "rev-parse", "--abbrev-ref", "HEAD")).Text;

    public async Task<bool> IsDirtyAsync(CancellationToken ct = default) =>
        (await RequireAsync(ct, null, "status", "--porcelain", "--untracked-files=no")).Text.Length > 0;

    public Task<Commit> HeadAsync(CancellationToken ct = default) => DescribeAsync("HEAD", null, ct);

    public Task<Commit> RemoteHeadAsync(string branch, CancellationToken ct = default) => DescribeAsync($"refs/remotes/origin/{branch}", branch, ct);

    /// <summary>Commits on the remote branch that HEAD lacks.</summary>
    public async Task<int> BehindAsync(string branch, CancellationToken ct = default) =>
        int.Parse((await RequireAsync(ct, null, "rev-list", "--count", $"HEAD..refs/remotes/origin/{branch}")).Text, CultureInfo.InvariantCulture);

    private async Task<Commit> DescribeAsync(string rev, string? branch, CancellationToken ct) {
        var text = (await RequireAsync(ct, null, "log", "-1", "--format=%H%n%cI%n%s", rev)).Output.Where(l => l.Length > 0).ToList();

        if (text.Count < 2) {
            throw new CommandException($"git log said nothing about {rev}");
        }

        return new Commit(text[0], branch ?? await CurrentBranchAsync(ct), DateTimeOffset.Parse(text[1], CultureInfo.InvariantCulture), text.Count > 2 ? text[2] : string.Empty);
    }

    /// <summary>
    /// Makes the working tree equal to <c>origin/&lt;branch&gt;</c>. Managed: check the branch out (creating or
    /// resetting the local one) and reset hard. Unmanaged: refuse a dirty tree, switch branches only when the tree
    /// is clean, and fast-forward only.
    /// </summary>
    public async Task<Commit> SyncAsync(string branch, bool managed, CancellationToken ct = default) {
        await FetchAsync(branch, ct);

        if (managed) {
            await RequireAsync(ct, null, "checkout", "--quiet", "-B", branch, $"refs/remotes/origin/{branch}");
            await RequireAsync(ct, null, "reset", "--quiet", "--hard", $"refs/remotes/origin/{branch}");
            return await HeadAsync(ct);
        }

        if (await IsDirtyAsync(ct)) {
            throw new CommandException($"{Directory} has uncommitted changes; commit or stash them, or let the plugin use its own clone");
        }

        if (await CurrentBranchAsync(ct) != branch) {
            var local = await RunAsync(ct, null, "rev-parse", "--verify", "--quiet", $"refs/heads/{branch}");

            if (local.Ok) {
                await RequireAsync(ct, null, "checkout", "--quiet", branch);
            } else {
                await RequireAsync(ct, null, "checkout", "--quiet", "--track", "-b", branch, $"refs/remotes/origin/{branch}");
            }
        }

        var merge = await RunAsync(ct, null, "merge", "--ff-only", "--quiet", $"refs/remotes/origin/{branch}");

        if (!merge.Ok) {
            throw new CommandException($"{branch} in {Directory} has diverged from origin; the plugin only fast-forwards your own checkout. {merge.Tail(2)}", merge);
        }

        return await HeadAsync(ct);
    }
}
