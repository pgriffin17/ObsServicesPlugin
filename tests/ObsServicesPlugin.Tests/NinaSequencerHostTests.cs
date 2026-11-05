using System.IO;
using System.Reflection;
using Moq;
using NINA.Profile.Interfaces;
using NINA.Sequencer.Interfaces.Mediator;
using NINA.Sequencer.Mediator;
using NINA.WPF.Base.Interfaces.Mediator;
using NUnit.Framework;
using ObsServicesPlugin.Nina;

namespace ObsServicesPlugin.Tests {

    /// <summary>Windows-only: the parts of the sequencer host that do not need a running N.I.N.A.</summary>
    public class NinaSequencerHostTests {
        [Test]
        public void TheMediatorStillHidesItsNavigationViewModelWhereWeLookForIt() {
            // The only unsupported thing the plugin does is read this private field to reach the sequencer
            // factory. If a N.I.N.A. release renames it, this fails here instead of at 2 a.m.
            var field = typeof(SequenceMediator).GetField(NinaSequencerHost.NavigationField, BindingFlags.Instance | BindingFlags.NonPublic);

            Assert.That(field, Is.Not.Null, $"SequenceMediator.{NinaSequencerHost.NavigationField} is gone; the host cannot deserialise programs");
            Assert.That(typeof(NINA.ViewModel.Sequencer.ISequenceNavigationVM).IsAssignableFrom(field!.FieldType), Is.True);
        }

        [Test]
        public void FactoryLookupExplainsItselfBeforeTheSequencerIsRegistered() {
            var ok = NinaSequencerHost.TryGetFactory(new SequenceMediator(), out var factory, out var reason);

            Assert.That(ok, Is.False);
            Assert.That(factory, Is.Null);
            Assert.That(reason, Does.Contain("not been registered"));
        }

        [Test]
        public void FormatsAreSniffedFromTheRootType() {
            Assert.That(NinaSequencerHost.FormatOf("""{"$type":"NINA.Sequencer.Container.SequenceRootContainer, NINA.Sequencer"}"""), Is.EqualTo("nina.sequence"));
            Assert.That(NinaSequencerHost.FormatOf("""{"$type":"NINA.Sequencer.Container.DeepSkyObjectContainer, NINA.Sequencer"}"""), Is.EqualTo("nina.target"));
            Assert.That(NinaSequencerHost.FormatOf("""{"$type":"NINA.Sequencer.Container.SequentialContainer, NINA.Sequencer"}"""), Is.EqualTo("nina.template"));
            Assert.That(NinaSequencerHost.FormatOf("""{"$type":"System.Diagnostics.Process, System"}"""), Is.Null);
            Assert.That(NinaSequencerHost.FormatOf("not json"), Is.Null);

            var reference = ReferenceSequence();

            if (reference != null) {
                Assert.That(NinaSequencerHost.FormatOf(reference), Is.EqualTo("nina.sequence"));
            }
        }

        [Test]
        public void ReadsDisconnectedUntilTheSequencerIsInitialised() {
            var mediator = new Mock<ISequenceMediator>();
            mediator.SetupGet(m => m.Initialized).Returns(false);
            var host = new NinaSequencerHost(mediator.Object, Mock.Of<IImageSaveMediator>(), Mock.Of<IProfileService>());

            Assert.That(host.Read().Connected, Is.False);
            Assert.That(host.Formats, Is.EqualTo(new[] { "nina.sequence", "nina.target", "nina.template" }));
            Assert.That(NinaSequencerHost.InstalledExtensions(), Is.Not.Null);
        }

        [Test]
        public void WholeSequencesAreRefusedUntilAllowed() {
            var mediator = new Mock<ISequenceMediator>();
            mediator.SetupGet(m => m.Initialized).Returns(true);
            var host = new NinaSequencerHost(mediator.Object, Mock.Of<IImageSaveMediator>(), Mock.Of<IProfileService>());
            var program = new ObsServices.Core.Protocol.ProgramRef { Id = "p", Format = "nina.sequence", Body = """{"$type":"NINA.Sequencer.Container.SequenceRootContainer, NINA.Sequencer"}""" };

            var ex = Assert.ThrowsAsync<InvalidOperationException>(() => host.RunProgramAsync(program, new ObsServices.Core.Protocol.ObservationTask(), new Progress<int>(), default));

            Assert.That(ex!.Message, Does.Contain("enable them in the plugin options"));
        }

        private static string? ReferenceSequence() {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);

            while (dir != null && !File.Exists(Path.Combine(dir.FullName, "ObsServicesPlugin.slnx")) && !File.Exists(Path.Combine(dir.FullName, "ObsServicesPlugin.sln"))) {
                dir = dir.Parent;
            }

            var path = dir == null ? null : Path.Combine(dir.FullName, "..", "telescope_control", "reference_nina_sequences", "M57_ObsServicesTestSequence.json");
            return path != null && File.Exists(path) ? File.ReadAllText(path) : null;
        }
    }
}
