using ObsServices.Core;
using ObsServices.Core.Devices;

namespace ObsServices.Core.Tests.Fakes;

/// <summary>
/// Software devices with the timing of real ones scaled down: a mount that slews at a rate and
/// tracks, a camera that takes the requested seconds times <see cref="TimeScale"/>, a roof, a
/// wheel and a focuser. The conformance reference for <see cref="Agent.ObservatoryAgent"/> on Linux.
/// </summary>
public sealed class FakeClock {
    /// <summary>Wall seconds per simulated second. 0.01 makes a 10 s exposure take 100 ms.</summary>
    public double TimeScale { get; set; } = 0.01;

    public Task SleepAsync(double simulatedSeconds, CancellationToken ct) => Task.Delay(TimeSpan.FromSeconds(simulatedSeconds * TimeScale), ct);
}

public sealed class FakeMount : IMountDevice {
    private readonly FakeClock clock;
    private readonly double lonDeg;
    private readonly double latDeg;
    private readonly object gate = new();
    private MountState state = MountState.Parked;
    private double ha;
    private double dec;
    private (double Ra, double Dec)? target;

    public FakeMount(FakeClock clock, double latDeg, double lonDeg) {
        this.clock = clock;
        this.latDeg = latDeg;
        this.lonDeg = lonDeg;
    }

    public bool Connected { get; set; } = true;

    public double SlewDegPerSecond { get; set; } = 4.0;

    public List<string> Calls { get; } = new();

    private double Lst => Astro.LstDeg(Protocol.Timestamp.Now(), lonDeg);

    public MountReading Read() {
        lock (gate) {
            if (!Connected) {
                return MountReading.Disconnected;
            }

            var lst = Lst;

            if (state == MountState.Tracking && target is { } t) {
                ha = Astro.HourAngleDeg(lst, t.Ra);
                dec = t.Dec;
            }

            var ra = Astro.RaDeg(lst, ha);
            var (alt, az) = Astro.AltAz(ha, dec, latDeg);
            return new MountReading(true, state, lst, ra, dec, ra, dec, alt, az);
        }
    }

    public async Task SlewAsync(double raJ2000Deg, double decJ2000Deg, CancellationToken ct) {
        double distance;

        lock (gate) {
            Calls.Add($"slew {raJ2000Deg:0.##} {decJ2000Deg:0.##}");

            if (state == MountState.Parked) {
                throw new InvalidOperationException("mount is parked");
            }

            var targetHa = Astro.HourAngleDeg(Lst, raJ2000Deg);
            distance = Math.Max(Math.Abs(targetHa - ha), Math.Abs(decJ2000Deg - dec));
            state = MountState.Slewing;
            target = (raJ2000Deg, decJ2000Deg);
        }

        try {
            await clock.SleepAsync(distance / SlewDegPerSecond, ct);
        } catch (OperationCanceledException) {
            lock (gate) state = MountState.Stopped;
            throw;
        }

        lock (gate) {
            ha = Astro.HourAngleDeg(Lst, raJ2000Deg);
            dec = decJ2000Deg;
            state = MountState.Tracking;
        }
    }

    public Task SetTrackingAsync(bool enabled, CancellationToken ct) {
        lock (gate) {
            Calls.Add($"tracking {enabled}");

            if (state != MountState.Parked) {
                state = enabled ? MountState.Tracking : MountState.Stopped;
            }
        }

        return Task.CompletedTask;
    }

    public async Task ParkAsync(CancellationToken ct) {
        lock (gate) {
            Calls.Add("park");
            state = MountState.Slewing;
            target = null;
        }

        await clock.SleepAsync(5, ct);

        lock (gate) {
            ha = 90;
            dec = latDeg >= 0 ? 90 : -90;
            state = MountState.Parked;
        }
    }

    public Task UnparkAsync(CancellationToken ct) {
        lock (gate) {
            Calls.Add("unpark");
            state = MountState.Stopped;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken ct) {
        lock (gate) {
            Calls.Add("stop");

            if (state != MountState.Parked) {
                state = MountState.Stopped;
                target = null;
            }
        }

        return Task.CompletedTask;
    }
}

public sealed class FakeCamera : ICameraDevice {
    private readonly FakeClock clock;
    private readonly object gate = new();
    private CameraActivity activity = CameraActivity.Idle;
    private double exposure;
    private DateTime exposureEnd;

    public FakeCamera(FakeClock clock) {
        this.clock = clock;
    }

    public bool Connected { get; set; } = true;

    public double ReadoutSeconds { get; set; } = 2.0;

    public List<ExposureRequest> Exposures { get; } = new();

    public CameraReading Read() {
        lock (gate) {
            if (!Connected) {
                return CameraReading.Disconnected;
            }

            var remaining = activity == CameraActivity.Exposing ? Math.Max(0, (exposureEnd - DateTime.UtcNow).TotalSeconds / clock.TimeScale) : 0;
            return new CameraReading(true, activity, -10.0, activity == CameraActivity.Exposing ? exposure : 0, remaining);
        }
    }

    public async Task ExposeAsync(ExposureRequest request, CancellationToken ct) {
        lock (gate) {
            activity = CameraActivity.Exposing;
            exposure = request.Seconds;
            exposureEnd = DateTime.UtcNow + TimeSpan.FromSeconds(request.Seconds * clock.TimeScale);
        }

        try {
            await clock.SleepAsync(request.Seconds, ct);
            lock (gate) activity = CameraActivity.ReadingOut;
            await clock.SleepAsync(ReadoutSeconds, ct);
            lock (gate) Exposures.Add(request);
        } finally {
            lock (gate) activity = CameraActivity.Idle;
        }
    }
}

public sealed class FakeFilterWheel : IFilterWheelDevice {
    private readonly FakeClock clock;

    public FakeFilterWheel(FakeClock clock, params string[] filters) {
        this.clock = clock;
        Filters = filters;
        CurrentFilter = filters.FirstOrDefault();
    }

    public bool Connected { get; set; } = true;

    public IReadOnlyList<string> Filters { get; }

    public string? CurrentFilter { get; private set; }

    public List<string> Changes { get; } = new();

    public async Task ChangeFilterAsync(string filter, CancellationToken ct) {
        if (!Filters.Contains(filter)) {
            throw new ArgumentException($"no filter {filter}");
        }

        await clock.SleepAsync(3, ct);
        CurrentFilter = filter;
        Changes.Add(filter);
    }
}

public sealed class FakeRoof : IRoofDevice {
    private readonly FakeClock clock;
    private RoofActivity activity = RoofActivity.Closed;

    public FakeRoof(FakeClock clock) {
        this.clock = clock;
    }

    public bool Connected { get; set; } = true;

    public double TravelSeconds { get; set; } = 20;

    public RoofActivity Read() => activity;

    public async Task OpenAsync(CancellationToken ct) {
        if (activity == RoofActivity.Open) {
            return;
        }

        activity = RoofActivity.Opening;
        await clock.SleepAsync(TravelSeconds, ct);
        activity = RoofActivity.Open;
    }

    public async Task CloseAsync(CancellationToken ct) {
        if (activity == RoofActivity.Closed) {
            return;
        }

        activity = RoofActivity.Closing;
        await clock.SleepAsync(TravelSeconds, ct);
        activity = RoofActivity.Closed;
    }
}

public sealed class FakeFocuser : IFocuserDevice {
    public bool Connected { get; set; } = true;

    public int? Position { get; set; } = 15000;
}
