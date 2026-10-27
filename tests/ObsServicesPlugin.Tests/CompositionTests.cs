using Moq;
using NUnit.Framework;
using NINA.Profile.Interfaces;
using NINA.Equipment.Interfaces.Mediator;
using NINA.Equipment.Interfaces.ViewModel;
using NINA.WPF.Base.Interfaces.Mediator;
using NINA.WPF.Base.Interfaces.ViewModel;
using NINA.Plugin.Interfaces;
using NINA.Sequencer.Logic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.Reflection;
using System.Text;
using System.Windows;

namespace ObsServicesPlugin.Tests {
    [NonParallelizable, Apartment(ApartmentState.STA)]
    public class CompositionTests {
        private static CompositionContainer Create(Mock<ITelescopeMediator> telescope) {
            var container = new CompositionContainer(new AssemblyCatalog(typeof(Plugin).Assembly));
            var profile = new Mock<IProfile>();
            profile.SetupGet(p => p.PluginSettings).Returns(new NINA.Profile.PluginSettings());
            var profiles = new Mock<IProfileService>();
            profiles.SetupGet(p => p.ActiveProfile).Returns(profile.Object);
            container.ComposeExportedValue(profiles.Object);
            container.ComposeExportedValue(telescope.Object);
            container.ComposeExportedValue(Mock.Of<IImageSaveMediator>());
            container.ComposeExportedValue(Mock.Of<IOptionsVM>());
            container.ComposeExportedValue(Mock.Of<IMessageBroker>());
            var broker = new Mock<ISymbolBroker>();
            broker.Setup(b => b.RegisterSymbolProvider(It.IsAny<string>())).Returns(Mock.Of<ISymbolProvider>());
            container.ComposeExportedValue(broker.Object);
            return container;
        }

        // MEF builds its error message lazily from the container. Once the "using" containers are
        // disposed, reading that message throws ObjectDisposedException and hides the real cause.
        // Catching inside the test, while the containers are still alive, reports the actual error.
        private static string DescribeCompositionFailure(CompositionException ex) {
            var report = new StringBuilder();
            report.AppendLine("MEF composition failed.");

            foreach (var error in ex.Errors) {
                report.AppendLine($"Error: {error.Description}");
            }

            foreach (var cause in ex.RootCauses) {
                report.AppendLine();
                report.AppendLine("Root cause:");
                report.AppendLine(cause.ToString());
            }

            return report.ToString();
        }

        [Test]
        public async Task ComposesManifestAndPartsInSeparateHostCatalogs() {
            var telescope = new Mock<ITelescopeMediator>();
            using var manifestCatalog = Create(telescope);
            using var partsCatalog = Create(telescope);

            try {
                var manifest = manifestCatalog.GetExportedValue<IPluginManifest>();
                var panels = partsCatalog.GetExportedValues<IDockableVM>().ToList();
                var items = partsCatalog.GetExportedValues<NINA.Sequencer.SequenceItem.ISequenceItem>().ToList();

                Assert.That(manifest, Is.InstanceOf<Plugin>());

                await manifest.Initialize();
                await manifest.Teardown();
            } catch (CompositionException ex) {
                Assert.Fail(DescribeCompositionFailure(ex));
            }
        }

        [Test]
        public void ExportedResourceDictionariesAndTemplatesConstruct() {
            // NINA drag/drop behaviors require an Application and a main window.
            if (Application.Current == null) new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var grid = new System.Windows.Controls.Grid();
            Application.Current.MainWindow = new Window { Content = grid };
            NameScope.SetNameScope(Application.Current.MainWindow, new NameScope());
            Application.Current.MainWindow.RegisterName("RootGrid", grid);
            foreach (var name in new[] { "ProfileService", "SVGDictionary", "Brushes", "Converters" }) {
                Application.Current.Resources.MergedDictionaries.Add(new ResourceDictionary {
                    Source = new Uri($"/NINA.WPF.Base;component/Resources/StaticResources/{name}.xaml", UriKind.Relative)
                });
            }
            foreach (var type in typeof(Plugin).Assembly.GetTypes().Where(t => typeof(ResourceDictionary).IsAssignableFrom(t))) {
                var dictionary = (ResourceDictionary)Activator.CreateInstance(type);
                foreach (var template in dictionary.Values.OfType<DataTemplate>()) {
                    Assert.That(template.LoadContent(), Is.Not.Null, type.FullName);
                }
            }
        }
    }
}