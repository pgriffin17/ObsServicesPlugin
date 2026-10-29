using System.IO;
using Moq;
using NINA.Astrometry;
using NINA.Equipment.Equipment.MyTelescope;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Profile;
using NINA.Profile.Interfaces;
using NUnit.Framework;
using ObsServices.Core.Devices;
using ObsServicesPlugin.Nina;

namespace ObsServicesPlugin.Tests {

    /// <summary>Windows-only (the N.I.N.A. assemblies need the desktop runtime), but compiles everywhere.</summary>
    public class PluginTests {
        private Mock<IProfileService> profiles = null!;
        private PluginSettings pluginSettings = null!;

        [SetUp]
        public void SetUp() {
            pluginSettings = new PluginSettings();
            var profile = new Mock<IProfile>();
            profile.SetupGet(p => p.PluginSettings).Returns(pluginSettings);
            profiles = new Mock<IProfileService>();
            profiles.SetupGet(p => p.ActiveProfile).Returns(profile.Object);
        }

        private Plugin Create() => new Plugin(profiles.Object, new DeviceSet());

        [Test]
        public void ManifestHasValidIdentityAndHostBaseline() {
            var plugin = Create();

            Assert.That(Guid.TryParse(plugin.Identifier, out _), Is.True);
            Assert.That(plugin.Name, Is.Not.Empty);
            Assert.That(plugin.MinimumApplicationVersion.ToString(), Is.EqualTo("3.3.0.1060"));
        }

        [Test]
        public void OptionsTemplateKeyMatchesThePluginTitle() {
            // N.I.N.A. looks the page up as "<Name>_Options" in the application resources.
            var xaml = File.ReadAllText(Path.Combine(FindSourceRoot(), "src", "ObsServicesPlugin", "Options.xaml"));

            Assert.That(xaml, Does.Contain($"x:Key=\"{Create().Name}_Options\""), "rename the DataTemplate key when the Title in Plugin.props changes");
        }

        [Test]
        public void SettingsRoundTripThroughTheProfile() {
            var plugin = Create();

            plugin.ServiceUrl = " http://localhost:7421/ ";
            plugin.SiteId = "oakland";
            plugin.AgentToken = "tca_secret";
            plugin.AutoConnect = true;
            plugin.StateIntervalSeconds = 0.5;

            var again = Create();
            Assert.That(again.ServiceUrl, Is.EqualTo("http://localhost:7421/"));
            Assert.That(again.SiteId, Is.EqualTo("oakland"));
            Assert.That(again.AgentToken, Is.EqualTo("tca_secret"));
            Assert.That(again.AutoConnect, Is.True);
            Assert.That(again.StateIntervalSeconds, Is.EqualTo(0.5));
            Assert.That(again.Settings.IsComplete, Is.True);
        }

        [Test]
        public void TelemetryOnlyIsTheDefaultAndPersists() {
            var plugin = Create();
            Assert.That(plugin.TelemetryOnly, Is.True, "a fresh install never accepts remote control");
            Assert.That(plugin.ModeDescription, Does.StartWith("Telemetry only"));

            plugin.TelemetryOnly = false;

            Assert.That(Create().TelemetryOnly, Is.False);
            Assert.That(plugin.ModeDescription, Does.StartWith("Full control"));
        }

        [Test]
        public async Task ConnectWithoutConfigurationReportsInsteadOfStarting() {
            var plugin = Create();

            await plugin.ConnectAsync();

            Assert.That(plugin.IsConnected, Is.False);
            Assert.That(plugin.Status, Is.EqualTo("Not configured"));
        }

        [Test]
        public async Task InitializeHonoursAutoConnectAndTeardownStops() {
            var plugin = Create();
            plugin.ServiceUrl = "http://127.0.0.1:1";   // nothing listens: the agent retries in the background
            plugin.SiteId = "site";
            plugin.AgentToken = "tca_x";
            plugin.AutoConnect = true;

            await plugin.Initialize();
            Assert.That(plugin.IsConnected, Is.True);
            Assert.That(plugin.Status, Does.StartWith("Connecting"));

            await plugin.Teardown();
            Assert.That(plugin.IsConnected, Is.False);
            Assert.That(plugin.Status, Is.EqualTo("Disconnected"));
        }

        private static string FindSourceRoot() {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ObsServicesPlugin.slnx"))) {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new DirectoryNotFoundException("repository root");
        }
    }

    public class NinaMountTests {
        [Test]
        public void ReadsHoursAsDegreesAndDerivesTheMode() {
            var mediator = new Mock<ITelescopeMediator>();
            mediator.Setup(m => m.GetInfo()).Returns(new TelescopeInfo {
                Connected = true,
                TrackingEnabled = true,
                SiderealTime = 6.0,
                RightAscension = 5.5,
                Declination = -5.4,
                Altitude = 40.0,
                Azimuth = 170.0,
                Coordinates = new Coordinates(Angle.ByHours(5.5), Angle.ByDegree(-5.4), Epoch.J2000),
                SideOfPier = NINA.Core.Enum.PierSide.pierWest,
            });
            var mount = new NinaMount(mediator.Object);

            var r = mount.Read();

            Assert.That(r.PierSide, Is.EqualTo(PierSide.West));

            Assert.That(r.Connected, Is.True);
            Assert.That(r.State, Is.EqualTo(MountState.Tracking));
            Assert.That(r.SiderealTimeDeg, Is.EqualTo(90.0));
            Assert.That(r.RightAscensionDeg, Is.EqualTo(82.5));
            Assert.That(r.DeclinationDeg, Is.EqualTo(-5.4));
            Assert.That(r.RightAscensionJ2000Deg, Is.EqualTo(82.5).Within(1e-9));
            Assert.That(r.AltitudeDeg, Is.EqualTo(40.0));
        }

        [Test]
        public void ParkedAndSlewingWinOverTracking() {
            var mediator = new Mock<ITelescopeMediator>();
            var info = new TelescopeInfo { Connected = true, TrackingEnabled = true, AtPark = true };
            mediator.Setup(m => m.GetInfo()).Returns(() => info);
            var mount = new NinaMount(mediator.Object);

            Assert.That(mount.Read().State, Is.EqualTo(MountState.Parked));
            info = new TelescopeInfo { Connected = true, TrackingEnabled = true, Slewing = true };
            Assert.That(mount.Read().State, Is.EqualTo(MountState.Slewing));
            info = new TelescopeInfo { Connected = true, SideOfPier = NINA.Core.Enum.PierSide.pierUnknown };
            Assert.That(mount.Read().PierSide, Is.EqualTo(PierSide.Unknown));
            info = new TelescopeInfo { Connected = false };
            Assert.That(mount.Read(), Is.EqualTo(MountReading.Disconnected));
        }

        [Test]
        public async Task SlewPassesJ2000CoordinatesAndFailsLoudly() {
            var mediator = new Mock<ITelescopeMediator>();
            Coordinates? sent = null;
            mediator.Setup(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>()))
                .Callback<Coordinates, CancellationToken>((c, _) => sent = c)
                .ReturnsAsync(true);
            var mount = new NinaMount(mediator.Object);

            await mount.SlewAsync(37.95, 89.26, default);

            Assert.That(sent!.Epoch, Is.EqualTo(Epoch.J2000));
            Assert.That(sent.RADegrees, Is.EqualTo(37.95).Within(1e-9));
            Assert.That(sent.Dec, Is.EqualTo(89.26).Within(1e-9));

            mediator.Setup(m => m.SlewToCoordinatesAsync(It.IsAny<Coordinates>(), It.IsAny<CancellationToken>())).ReturnsAsync(false);
            Assert.ThrowsAsync<InvalidOperationException>(() => mount.SlewAsync(0, 0, default));
        }
    }
}
