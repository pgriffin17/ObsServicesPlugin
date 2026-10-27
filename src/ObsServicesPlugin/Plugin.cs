// using NINA.Astrometry;
// using NINA.Core.Utility;
// using NINA.Equipment.Interfaces.Mediator;
// using NINA.Plugin;
// using NINA.Plugin.Interfaces;
// using System.ComponentModel.Composition;
using NINA.Astrometry;                        // Coordinates, Epoch
using NINA.Core.Utility;                      // Logger
using NINA.Core.Utility.Notification;         // Notification popups
using NINA.Equipment.Equipment.MyTelescope;   // TelescopeInfo
using NINA.Equipment.Interfaces.Mediator;     // ITelescopeMediator, ITelescopeConsumer
using NINA.Plugin;                            // PluginBase
using NINA.Plugin.Interfaces;                 // IPluginManifest
using System.ComponentModel.Composition;      // Export, ImportingConstructor

namespace ObsServicesPlugin {
    [Export(typeof(IPluginManifest))]
        public class Plugin : PluginBase {
        private readonly ITelescopeMediator telescopeMediator;
        private MountPositionLogger mountPositionLogger;

        [ImportingConstructor]
        public Plugin(ITelescopeMediator telescopeMediator) {
            this.telescopeMediator = telescopeMediator;
        }

        public void LogMountPosition() {
            var info = telescopeMediator.GetInfo();
            if (info == null || !info.Connected) {
                Logger.Warning("Mount not connected");
                return;
            }

            Logger.Info($"RA {info.RightAscensionString}  Dec {info.DeclinationString}");

            var j2000 = info.Coordinates.Transform(Epoch.J2000);
            Logger.Info($"J2000: RA {j2000.RA} h, Dec {j2000.Dec} deg");
        }

        public override Task Initialize() {
            Console.WriteLine("ObsServicesPlugin initialized");
            Logger.Info("ObsServicesPlugin initialized!!!!!!");
            this.mountPositionLogger = new MountPositionLogger(telescopeMediator, TimeSpan.FromSeconds(90));
            return Task.CompletedTask;
        }

        public override Task Teardown() {
            this.mountPositionLogger?.Dispose();
            return Task.CompletedTask;
        }
    }

    public class MountPositionLogger : ITelescopeConsumer {
        private readonly ITelescopeMediator telescopeMediator;
        private readonly DateTime stopAt;

        public MountPositionLogger(ITelescopeMediator telescopeMediator, TimeSpan timeout) {
            this.telescopeMediator = telescopeMediator;
            stopAt = DateTime.UtcNow + timeout;

            telescopeMediator.RegisterConsumer(this);
        }

        public void UpdateDeviceInfo(TelescopeInfo info) {
            if (DateTime.UtcNow > stopAt) {
                Dispose();
                return;
            }

            if (!info.Connected) {
                return;
            }

            Logger.Info($"RA {info.RightAscensionString}  Dec {info.DeclinationString}");
        }

        public void Dispose() {
            telescopeMediator.RemoveConsumer(this);
        }
    }
}