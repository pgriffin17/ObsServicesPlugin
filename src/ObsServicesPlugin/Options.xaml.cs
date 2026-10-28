using System.ComponentModel.Composition;
using System.Windows;

namespace ObsServicesPlugin;

/// <summary>Merged into the application resources by the plugin loader; holds the options page template.</summary>
[Export(typeof(ResourceDictionary))]
public partial class Options : ResourceDictionary {
    public Options() {
        InitializeComponent();
    }
}
