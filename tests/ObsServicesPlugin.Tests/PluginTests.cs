// using NUnit.Framework;
// using System.ComponentModel.Composition;
// using System.Reflection;
// using System.Runtime.InteropServices;

// namespace ObsServicesPlugin.Tests {
//     public class PluginTests {
//         [Test]
//         public void ManifestHasValidIdentityAndHostBaseline() {
//             var plugin = new Plugin();
//             Assert.That(Guid.TryParse(plugin.Identifier, out _), Is.True);
//             Assert.That(plugin.Name, Is.Not.Empty);
//             Assert.That(plugin.MinimumApplicationVersion.ToString(), Is.EqualTo("3.3.0.1060"));
//             Assert.That(typeof(Plugin).GetCustomAttributes<ExportAttribute>().Single().ContractType,
//                 Is.EqualTo(typeof(NINA.Plugin.Interfaces.IPluginManifest)));
//         }
//         [Test]
//         public void AssemblyMetadataUsesDisplayNameAndDisablesComExposure() {
//             var assembly = typeof(Plugin).Assembly;
//             Assert.Multiple(() => {
//                 Assert.That(assembly.GetCustomAttribute<AssemblyProductAttribute>()?.Product, Is.EqualTo(new Plugin().Name));
//                 Assert.That(assembly.GetCustomAttribute<ComVisibleAttribute>()?.Value, Is.False);
//             });
//         }
//     }
// }
using Moq;
using NINA.Astrometry;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NUnit.Framework;

namespace ObsServicesPlugin.Tests {

    public class PluginTests {
        private Mock<ITelescopeMediator> telescopeMediator;

        [SetUp]
        public void SetUp() {
            telescopeMediator = new Mock<ITelescopeMediator>();
        }

        [Test]
        public void Constructs() {
            var plugin = new Plugin(telescopeMediator.Object);

            Assert.That(plugin, Is.Not.Null);
        }

        [Test]
        public void LogMountPosition_ReadsMountInfo() {
            telescopeMediator
                .Setup(m => m.GetInfo())
                .Returns(new TelescopeInfo {
                    Connected = true,
                    RightAscensionString = "05:35:17",
                    DeclinationString = "-05:23:28",
                    Coordinates = new Coordinates(Angle.ByHours(5.588), Angle.ByDegree(-5.391), Epoch.J2000)
                });

            var plugin = new Plugin(telescopeMediator.Object);

            plugin.LogMountPosition();

            telescopeMediator.Verify(m => m.GetInfo(), Times.Once);
        }
    }
}