namespace ObsServices.Core.Update;

/// <summary>
/// The package manifest the <c>StageForNina</c> target writes next to the plugin's files, and what it is for:
/// N.I.N.A. installs a staged update by adding and replacing files, never removing, so a file this version no
/// longer ships would linger and still be loaded. The manifest says what belongs; the rest is stale.
/// </summary>
public static class StaleFiles {
    public const string ManifestName = ".obsservices-files";

    /// <summary>The relative paths listed in the manifest under <paramref name="pluginDirectory"/>, or null when there is none.</summary>
    public static IReadOnlySet<string>? ReadManifest(string pluginDirectory) {
        var path = Path.Combine(pluginDirectory, ManifestName);

        if (!File.Exists(path)) {
            return null;
        }

        return File.ReadAllLines(path).Select(l => l.Trim().TrimStart('\uFEFF')).Where(l => l.Length > 0).Select(Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Files in the plugin's own folder (one level, where N.I.N.A. looks for plugin assemblies) that the manifest does not
    /// list. Only assemblies and their symbols are reported: anything else in the folder may be the user's.
    /// Empty without a manifest.
    /// </summary>
    public static IReadOnlyList<string> Find(string pluginDirectory) {
        var manifest = ReadManifest(pluginDirectory);

        if (manifest == null || !Directory.Exists(pluginDirectory)) {
            return Array.Empty<string>();
        }

        return Directory.EnumerateFiles(pluginDirectory)
            .Where(f => f.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
            .Where(f => !manifest.Contains(Normalize(Path.GetFileName(f))))
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string Normalize(string relative) => relative.Replace('\\', '/').TrimStart('/');
}
