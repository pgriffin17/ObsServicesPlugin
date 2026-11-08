using System.IO;
using NUnit.Framework;
using ObsServicesPlugin.Nina;

namespace ObsServicesPlugin.Tests {

    /// <summary>The path rules of the in-app updater; the git and build steps are tested in the core.</summary>
    public class NinaUpdaterTests {
        [Test]
        public void StagesIntoTheFolderThePluginWasLoadedFrom() {
            var plugins = Path.Combine(Path.GetTempPath(), "NINA", "Plugins", "3.0.0");
            Assert.That(NinaUpdater.PluginFolderName(Path.Combine(plugins, "Obs Services Test Plugin"), plugins, "Other Title"), Is.EqualTo("Obs Services Test Plugin"));
            Assert.That(NinaUpdater.PluginFolderName(Path.Combine(plugins, "renamed") + Path.DirectorySeparatorChar, plugins + Path.DirectorySeparatorChar, "Other Title"), Is.EqualTo("renamed"));
        }

        [Test]
        public void FallsBackToTheTitleOutsideThePluginsFolder() {
            var plugins = Path.Combine(Path.GetTempPath(), "NINA", "Plugins", "3.0.0");
            Assert.That(NinaUpdater.PluginFolderName(Path.Combine(Path.GetTempPath(), "dev", "bin"), plugins, "Obs Services Test Plugin"), Is.EqualTo("Obs Services Test Plugin"));
            Assert.That(NinaUpdater.PluginFolderName(plugins, plugins, "T"), Is.EqualTo("T"), "a plugin dll in the root of the plugins folder has no folder of its own");
            Assert.That(NinaUpdater.PluginFolderName(Path.Combine(plugins, "a", "deeper"), plugins, "T"), Is.EqualTo("T"), "N.I.N.A. loads one level deep only");
            Assert.That(NinaUpdater.PluginFolderName(string.Empty, plugins, "T"), Is.EqualTo("T"));
        }

        [Test]
        public void TitleBecomesAFolderNameAsTheDeployScriptMakesIt() {
            Assert.That(NinaUpdater.SafeFolderName("Obs Services Test Plugin"), Is.EqualTo("Obs Services Test Plugin"));
            Assert.That(NinaUpdater.SafeFolderName("A/B\\C"), Is.EqualTo("A-B-C"));
            Assert.That(NinaUpdater.SafeFolderName("What?"), Is.EqualTo(OperatingSystem.IsWindows() ? "What_" : "What?"));
            Assert.Throws<InvalidOperationException>(() => NinaUpdater.SafeFolderName("  "));
        }

        [Test]
        public void SourceFolderDefaultsToThePluginsOwnClone() {
            Assert.That(NinaUpdater.ResolveSourceDirectory(string.Empty), Does.EndWith(Path.Combine("ObsServices", "src")));
            Assert.That(NinaUpdater.ResolveSourceDirectory(@"C:\src\ObsServicesPlugin"), Is.EqualTo(@"C:\src\ObsServicesPlugin"));
        }
    }
}
