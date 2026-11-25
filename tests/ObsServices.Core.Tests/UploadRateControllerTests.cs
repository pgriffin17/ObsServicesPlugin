using NUnit.Framework;
using ObsServices.Core.Imaging;

namespace ObsServices.Core.Tests;

/// <summary>The upload rate learned from the socket's health: up a tenth per quiet window while uploading, halved on a slow send, held after a stall.</summary>
public class UploadRateControllerTests {
    [Test]
    public void GrowsWhileUploadingQuietlyAndOnlyThen() {
        var c = new UploadRateController();
        var changes = new List<long>();
        c.Changed += changes.Add;
        Assert.That(c.Current, Is.EqualTo(250_000));

        // idle: nothing learned
        for (var t = 0; t < 120; t++) {
            c.Tick(t, uploading: false, achievedBps: null);
        }

        Assert.That(changes, Is.Empty);

        // uploading quietly: one increase per 30 s window
        for (var t = 120; t <= 120 + 90; t++) {
            c.Tick(t, uploading: true, achievedBps: 240_000);
        }

        Assert.That(changes, Is.EqualTo(new long[] { 275_000, 302_500, 332_750 }));
    }

    [Test]
    public void HalvesOnASlowSendOnceInTenSecondsAndHoldsAfterAStall() {
        var c = new UploadRateController();
        c.Reset(1_000_000);
        c.OnSend(TimeSpan.FromSeconds(2), now: 100);
        Assert.That(c.Current, Is.EqualTo(500_000));
        c.OnSend(TimeSpan.FromSeconds(2), now: 105);
        Assert.That(c.Current, Is.EqualTo(500_000), "one decrease per ten seconds");
        c.OnSend(TimeSpan.FromMilliseconds(200), now: 111);
        Assert.That(c.Current, Is.EqualTo(500_000), "a quick send is not trouble");
        c.OnSend(TimeSpan.FromSeconds(2), now: 112);
        Assert.That(c.Current, Is.EqualTo(250_000));

        // a stall: halved, and no increase for three minutes even while uploading quietly
        c.OnStall(now: 130);
        Assert.That(c.Current, Is.EqualTo(125_000));

        for (var t = 130; t <= 130 + 170; t++) {
            c.Tick(t, uploading: true, achievedBps: 120_000);
        }

        Assert.That(c.Current, Is.EqualTo(125_000), "held");

        for (var t = 300; t <= 300 + 31; t++) {
            c.Tick(t, uploading: true, achievedBps: 120_000);
        }

        Assert.That(c.Current, Is.GreaterThan(125_000), "growing again once the hold is over");
    }

    [Test]
    public void FollowsALinkThatGivesMuchLessAndRespectsTheBounds() {
        var c = new UploadRateController();
        c.Reset(4_000_000);

        // the link delivers a fifth of the rate for a whole window: the rate comes down to it plus headroom
        for (var t = 0; t <= 31; t++) {
            c.Tick(t, uploading: true, achievedBps: 800_000);
        }

        Assert.That(c.Current, Is.EqualTo(1_000_000));

        // floor and ceiling
        for (var i = 0; i < 20; i++) {
            c.OnStall(now: 1000 + i * 20);
        }

        Assert.That(c.Current, Is.EqualTo(c.Floor));
        c.Reset(long.MaxValue);
        Assert.That(c.Current, Is.EqualTo(c.Max));
        c.Ceiling = 500_000;
        Assert.That(c.Effective, Is.EqualTo(500_000), "the user's ceiling applies on top of what is learned");
    }
}
