using NUnit.Framework;
using ObsServices.Core.Protocol;
using ObsServicesPlugin.Nina;

namespace ObsServicesPlugin.Tests {

    /// <summary>The pieces of the frame hook that need no N.I.N.A. runtime.</summary>
    public class NinaFrameSourceTests {
        [Test]
        public void SharingFollowsTheKindSwitches() {
            var off = new FrameSharing(false, true, true);
            Assert.That(off.Shares(ImageKind.Light), Is.False);

            var lightsOnly = new FrameSharing(true, false, false);
            Assert.That(lightsOnly.Shares(ImageKind.Light), Is.True);
            Assert.That(lightsOnly.Shares(ImageKind.Dark), Is.False);
            Assert.That(lightsOnly.Shares(ImageKind.Other), Is.False);

            var all = new FrameSharing(true, true, true);
            Assert.That(new[] { ImageKind.Bias, ImageKind.Dark, ImageKind.Flat, ImageKind.Light, ImageKind.Other }.All(all.Shares), Is.True);
        }

        [Test]
        public void ExposureStartIsReadAsLocalTimeUnlessMarkedUtc() {
            var utc = new DateTime(2026, 11, 7, 3, 0, 0, DateTimeKind.Utc);
            Assert.That(NinaFrameSource.UnixSeconds(utc), Is.EqualTo(new DateTimeOffset(utc).ToUnixTimeSeconds()));

            var local = new DateTime(2026, 11, 7, 3, 0, 0, DateTimeKind.Local);
            Assert.That(NinaFrameSource.UnixSeconds(local), Is.EqualTo(new DateTimeOffset(local).ToUnixTimeSeconds()));

            // N.I.N.A. hands over DateTime.Now, whose kind is Local; an unspecified kind is read the same way
            var unspecified = new DateTime(2026, 11, 7, 3, 0, 0, DateTimeKind.Unspecified);
            Assert.That(NinaFrameSource.UnixSeconds(unspecified), Is.EqualTo(NinaFrameSource.UnixSeconds(local)));

            Assert.That(NinaFrameSource.UnixSeconds(DateTime.MinValue), Is.EqualTo(Timestamp.Now()).Within(5), "no stamp: now");
        }

        [Test]
        public void ImageTypesMapToKinds() {
            Assert.That(ImageKindExtensions.ParseImageKind("LIGHT"), Is.EqualTo(ImageKind.Light));
            Assert.That(ImageKindExtensions.ParseImageKind("DARKFLAT"), Is.EqualTo(ImageKind.Dark));
            Assert.That(ImageKindExtensions.ParseImageKind("SNAPSHOT"), Is.EqualTo(ImageKind.Other));
            Assert.That(ImageKindExtensions.ParseImageKind(null), Is.EqualTo(ImageKind.Other));
        }
    }
}
