namespace ObsServices.Core.Devices;

/// <summary>
/// The hardware the generic agent drives, as small interfaces so the N.I.N.A. plugin, a simulator
/// and the tests can each implement them. Readings are snapshots of the latest known state; the
/// agent samples them on its publish cadence. Angles are degrees; right ascension is in degrees too.
/// </summary>
public enum MountState {
    Disconnected,
    Parked,
    Slewing,
    Tracking,
    Stopped,
}

/// <summary>
/// Which side of the pier the tube is on, for German equatorials. Same meaning as ASCOM's
/// SideOfPier: East is the normal pose (used west of the meridian), West the flipped pose.
/// </summary>
public enum PierSide {
    Unknown,
    East,
    West,
}

/// <summary>What a mount reports right now. Nulls mean "not available".</summary>
public sealed record MountReading(
    bool Connected,
    MountState State,
    /// <summary>Local apparent sidereal time, degrees.</summary>
    double? SiderealTimeDeg,
    /// <summary>Right ascension of date (the frame the sidereal time applies to), degrees.</summary>
    double? RightAscensionDeg,
    /// <summary>Declination of date, degrees.</summary>
    double? DeclinationDeg,
    /// <summary>Right ascension in J2000, degrees, for display next to catalogue targets.</summary>
    double? RightAscensionJ2000Deg,
    double? DeclinationJ2000Deg,
    double? AltitudeDeg,
    double? AzimuthDeg,
    /// <summary>German equatorials: which side the tube is on. Unknown for other mounts or when the driver cannot say.</summary>
    PierSide PierSide = PierSide.Unknown) {
    public static readonly MountReading Disconnected = new(false, MountState.Disconnected, null, null, null, null, null, null, null);
}

public interface IMountDevice {
    MountReading Read();

    /// <summary>Slew to J2000 coordinates and return once the mount has settled.</summary>
    Task SlewAsync(double raJ2000Deg, double decJ2000Deg, CancellationToken cancellationToken);

    Task SetTrackingAsync(bool enabled, CancellationToken cancellationToken);

    Task ParkAsync(CancellationToken cancellationToken);

    Task UnparkAsync(CancellationToken cancellationToken);

    /// <summary>Abort any slew and hold position.</summary>
    Task StopAsync(CancellationToken cancellationToken);
}

public enum CameraActivity {
    Disconnected,
    Idle,
    Exposing,
    ReadingOut,
}

public sealed record CameraReading(bool Connected, CameraActivity Activity, double? TemperatureC, double ExposureSeconds, double ExposureRemainingSeconds) {
    public static readonly CameraReading Disconnected = new(false, CameraActivity.Disconnected, null, 0, 0);
}

/// <summary>One frame to take, with the context the host may want to write into the file.</summary>
public sealed record ExposureRequest(double Seconds, string Filter, string TargetName, double RaJ2000Deg, double DecJ2000Deg, string TaskId, string TaskName, int FrameIndex, int FrameCount);

public interface ICameraDevice {
    CameraReading Read();

    /// <summary>Take and store one frame; returns when it is downloaded. Cancellation aborts the exposure.</summary>
    Task ExposeAsync(ExposureRequest request, CancellationToken cancellationToken);
}

public interface IFilterWheelDevice {
    bool Connected { get; }

    IReadOnlyList<string> Filters { get; }

    string? CurrentFilter { get; }

    Task ChangeFilterAsync(string filter, CancellationToken cancellationToken);
}

public enum RoofActivity {
    Unknown,
    Closed,
    Opening,
    Open,
    Closing,
}

/// <summary>A roll-off roof or a dome shutter: whatever stands between the telescope and the sky.</summary>
public interface IRoofDevice {
    bool Connected { get; }

    RoofActivity Read();

    Task OpenAsync(CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}

public interface IFocuserDevice {
    bool Connected { get; }

    int? Position { get; }
}

/// <summary>What a host has plugged in. Any member may be null (not present) or report disconnected.</summary>
public interface IDeviceSet {
    IMountDevice? Mount { get; }

    ICameraDevice? Camera { get; }

    IFilterWheelDevice? FilterWheel { get; }

    IRoofDevice? Roof { get; }

    IFocuserDevice? Focuser { get; }
}

/// <summary>A plain bag of devices for hosts that compose them by hand.</summary>
public sealed class DeviceSet : IDeviceSet {
    public IMountDevice? Mount { get; init; }

    public ICameraDevice? Camera { get; init; }

    public IFilterWheelDevice? FilterWheel { get; init; }

    public IRoofDevice? Roof { get; init; }

    public IFocuserDevice? Focuser { get; init; }
}
