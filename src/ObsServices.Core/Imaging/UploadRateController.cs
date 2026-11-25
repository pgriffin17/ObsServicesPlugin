namespace ObsServices.Core.Imaging;

/// <summary>
/// Learns how fast frames may leave without starving the agent socket. The uplink's speed is not the point;
/// whether telemetry and task reports still get through while uploads run is, and the socket says so
/// directly: a send that takes long means the link is congested, a stalled session is the hard version.
/// Additive increase, multiplicative decrease, the rule TCP applies to every flow, applied to the one flow
/// the agent controls. Everything is in bytes per second and unix seconds; the caller supplies the clock.
/// </summary>
public sealed class UploadRateController {
    private double windowStart = double.NaN;
    private double lastDecrease = double.NegativeInfinity;
    private double holdUntil = double.NegativeInfinity;
    private bool troubleInWindow;

    public UploadRateController() {
        Current = Initial;
    }

    /// <summary>Where a fresh install starts: 2 Mbit/s.</summary>
    public long Initial { get; init; } = 250_000;

    /// <summary>Never below this, so uploads never stop entirely: 256 kbit/s.</summary>
    public long Floor { get; init; } = 32_000;

    /// <summary>Never above this when learning; the user's own cap (<see cref="Ceiling"/>) is applied on top.</summary>
    public long Max { get; init; } = 125_000_000;

    /// <summary>The user's cap, bytes per second; null or 0 = none.</summary>
    public long? Ceiling { get; set; }

    /// <summary>A quiet window this long while uploading earns an increase.</summary>
    public TimeSpan Window { get; init; } = TimeSpan.FromSeconds(30);

    public double Increase { get; init; } = 1.10;

    /// <summary>A socket send slower than this is congestion.</summary>
    public TimeSpan SlowSend { get; init; } = TimeSpan.FromSeconds(1.5);

    /// <summary>At most one decrease per this long, so one bad second does not collapse the rate.</summary>
    public TimeSpan DecreaseHoldoff { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>After a stall, no increase for this long.</summary>
    public TimeSpan StallHold { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>The learned rate, bytes per second.</summary>
    public long Current { get; private set; }

    /// <summary>What uploads may use now: the learned rate under the user's ceiling.</summary>
    public long Effective => Ceiling is { } c && c > 0 ? Math.Min(Current, c) : Current;

    /// <summary>Fires with the new <see cref="Current"/> whenever it changes; the host persists it.</summary>
    public event Action<long>? Changed;

    /// <summary>Start from a remembered rate (last night's), clamped to the bounds.</summary>
    public void Reset(long learned) => Set(learned);

    /// <summary>A message went onto the socket in <paramref name="took"/>.</summary>
    public void OnSend(TimeSpan took, double now) {
        if (took >= SlowSend) {
            Decrease(now);
        }
    }

    /// <summary>The socket stalled outright: halve and hold.</summary>
    public void OnStall(double now) {
        holdUntil = now + StallHold.TotalSeconds;
        Decrease(now);
    }

    /// <summary>
    /// Call about once a second. Adapts only while uploads are running: a quiet window earns an increase, and a
    /// window whose achieved throughput stayed far under the rate means the link itself is the limit, so the
    /// rate follows it down with headroom. <paramref name="achievedBps"/> is the measured upload throughput
    /// over roughly the window, or null when unknown.
    /// </summary>
    public void Tick(double now, bool uploading, double? achievedBps) {
        if (!uploading || double.IsNaN(windowStart)) {
            windowStart = now;
            troubleInWindow = false;
            return;
        }

        if (now - windowStart < Window.TotalSeconds) {
            return;
        }

        if (!troubleInWindow && now >= holdUntil) {
            if (achievedBps is { } a && a > 0 && a < 0.5 * Effective) {
                // link-bound well under the cap: pacing is not what limits us, the link is; leave room on it
                Set(Math.Max(Floor, (long)(a * 1.25)));
            } else {
                Set((long)(Current * Increase));
            }
        }

        windowStart = now;
        troubleInWindow = false;
    }

    private void Decrease(double now) {
        troubleInWindow = true;

        if (now - lastDecrease < DecreaseHoldoff.TotalSeconds) {
            return;
        }

        lastDecrease = now;
        Set(Current / 2);
    }

    private void Set(long value) {
        var clamped = Math.Clamp(value, Floor, Max);

        if (clamped != Current) {
            Current = clamped;
            Changed?.Invoke(clamped);
        }
    }
}
