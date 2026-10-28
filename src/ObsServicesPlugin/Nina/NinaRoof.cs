using NINA.Equipment.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using ObsServices.Core.Devices;

namespace ObsServicesPlugin.Nina;

/// <summary>The N.I.N.A. dome's shutter as the roof. A roll-off roof driver presents itself as a dome shutter in ASCOM.</summary>
public sealed class NinaRoof : IRoofDevice {
    private readonly IDomeMediator dome;

    public NinaRoof(IDomeMediator dome) {
        this.dome = dome;
    }

    public bool Connected => dome.GetInfo()?.Connected == true;

    public RoofActivity Read() {
        var info = dome.GetInfo();

        if (info == null || !info.Connected) {
            return RoofActivity.Unknown;
        }

        return info.ShutterStatus switch {
            ShutterState.ShutterOpen => RoofActivity.Open,
            ShutterState.ShutterOpening => RoofActivity.Opening,
            ShutterState.ShutterClosed => RoofActivity.Closed,
            ShutterState.ShutterClosing => RoofActivity.Closing,
            _ => RoofActivity.Unknown,
        };
    }

    public async Task OpenAsync(CancellationToken cancellationToken) {
        if (!await dome.OpenShutter(cancellationToken)) {
            throw new InvalidOperationException("N.I.N.A. could not open the shutter");
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken) {
        if (!await dome.CloseShutter(cancellationToken)) {
            throw new InvalidOperationException("N.I.N.A. could not close the shutter");
        }
    }
}
