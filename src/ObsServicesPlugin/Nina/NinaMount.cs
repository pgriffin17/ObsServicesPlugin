using NINA.Astrometry;
using NINA.Core.Model;
using NINA.Equipment.Interfaces.Mediator;
using ObsServices.Core;
using ObsServices.Core.Devices;

namespace ObsServicesPlugin.Nina;

/// <summary>The N.I.N.A. mount as the core sees it, through <see cref="ITelescopeMediator"/>.</summary>
public sealed class NinaMount : IMountDevice {
    private static readonly IProgress<ApplicationStatus> NoProgress = new Progress<ApplicationStatus>();
    private readonly ITelescopeMediator telescope;

    public NinaMount(ITelescopeMediator telescope) {
        this.telescope = telescope;
    }

    public MountReading Read() {
        var info = telescope.GetInfo();

        if (info == null || !info.Connected) {
            return MountReading.Disconnected;
        }

        var state = info.AtPark ? MountState.Parked
            : info.Slewing ? MountState.Slewing
            : info.TrackingEnabled ? MountState.Tracking
            : MountState.Stopped;

        // TelescopeInfo reports RA and sidereal time in hours, in the mount's own equatorial system.
        double? lst = double.IsNaN(info.SiderealTime) ? null : Astro.HoursToDeg(info.SiderealTime);
        double? ra = double.IsNaN(info.RightAscension) ? null : Astro.HoursToDeg(info.RightAscension);
        double? dec = double.IsNaN(info.Declination) ? null : info.Declination;
        double? raJ2000 = null;
        double? decJ2000 = null;

        if (info.Coordinates != null) {
            var j2000 = info.Coordinates.Transform(Epoch.J2000);
            raJ2000 = j2000.RADegrees;
            decJ2000 = j2000.Dec;
        }

        double? alt = double.IsNaN(info.Altitude) ? null : info.Altitude;
        double? az = double.IsNaN(info.Azimuth) ? null : info.Azimuth;
        var pierSide = info.SideOfPier switch {
            NINA.Core.Enum.PierSide.pierEast => PierSide.East,
            NINA.Core.Enum.PierSide.pierWest => PierSide.West,
            _ => PierSide.Unknown,
        };
        return new MountReading(true, state, lst, ra, dec, raJ2000, decJ2000, alt, az, pierSide);
    }

    public async Task SlewAsync(double raJ2000Deg, double decJ2000Deg, CancellationToken cancellationToken) {
        var coordinates = new Coordinates(Angle.ByDegree(raJ2000Deg), Angle.ByDegree(decJ2000Deg), Epoch.J2000);
        // The mediator transforms to the mount's equatorial system and waits for the slew to settle.
        var ok = await telescope.SlewToCoordinatesAsync(coordinates, cancellationToken);

        if (!ok) {
            throw new InvalidOperationException("N.I.N.A. refused the slew (mount parked, disconnected, or below the horizon?)");
        }
    }

    public Task SetTrackingAsync(bool enabled, CancellationToken cancellationToken) {
        telescope.SetTrackingEnabled(enabled);
        return Task.CompletedTask;
    }

    public async Task ParkAsync(CancellationToken cancellationToken) {
        if (!await telescope.ParkTelescope(NoProgress, cancellationToken)) {
            throw new InvalidOperationException("N.I.N.A. could not park the mount");
        }
    }

    public async Task UnparkAsync(CancellationToken cancellationToken) {
        if (!await telescope.UnparkTelescope(NoProgress, cancellationToken)) {
            throw new InvalidOperationException("N.I.N.A. could not unpark the mount");
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        telescope.StopSlew();
        return Task.CompletedTask;
    }
}
