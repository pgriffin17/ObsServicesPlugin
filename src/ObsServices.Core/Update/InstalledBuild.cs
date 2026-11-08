using System.Globalization;
using System.Reflection;

namespace ObsServices.Core.Update;

/// <summary>
/// What the running assembly was built from, read from the <c>GitCommit</c>, <c>GitBranch</c>, <c>GitDate</c> and
/// <c>GitDirty</c> metadata the build stamps (see the <c>ObsGitStamp</c> target). Unknown when built outside a
/// checkout or with the stamp switched off.
/// </summary>
public sealed record InstalledBuild(string Version, string? Commit, string? Branch, DateTimeOffset? When, bool Dirty) {
    public bool Known => !string.IsNullOrEmpty(Commit);

    public string? Short => Commit == null ? null : Commit.Length > 7 ? Commit[..7] : Commit;

    /// <summary>True when this build is of the given commit (any dirty tree counts as not).</summary>
    public bool Is(Commit commit) => Known && !Dirty && string.Equals(Commit, commit.Sha, StringComparison.OrdinalIgnoreCase);

    public override string ToString() {
        if (!Known) {
            return $"{Version} (commit unknown)";
        }

        var when = When == null ? string.Empty : $", {When.Value.ToLocalTime():yyyy-MM-dd HH:mm}";
        return $"{Version} from {Branch}@{Short}{(Dirty ? " with local changes" : string.Empty)}{when}";
    }

    public static InstalledBuild Of(Assembly assembly) {
        var meta = assembly.GetCustomAttributes<AssemblyMetadataAttribute>().GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First().Value);
        var version = assembly.GetCustomAttribute<AssemblyFileVersionAttribute>()?.Version ?? assembly.GetName().Version?.ToString() ?? "0.0.0.0";
        meta.TryGetValue("GitCommit", out var commit);
        meta.TryGetValue("GitBranch", out var branch);
        meta.TryGetValue("GitDate", out var date);
        meta.TryGetValue("GitDirty", out var dirty);
        DateTimeOffset? when = DateTimeOffset.TryParse(date, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed) ? parsed : null;
        return new InstalledBuild(version, string.IsNullOrWhiteSpace(commit) ? null : commit, string.IsNullOrWhiteSpace(branch) ? null : branch, when, string.Equals(dirty, "true", StringComparison.OrdinalIgnoreCase));
    }
}
