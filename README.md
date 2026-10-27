# ObsServicesPlugin

A plugin for NINA 3.3.0.1060 or later, targeting .NET 10. Metadata lives in Plugin.props. The plugin GUID is permanent.

## Develop

Install the .NET 10.0.4xx SDK and PowerShell 7 on Windows. Open ObsServicesPlugin.slnx in a compatible Visual Studio 2026 installation or use the CLI.

```powershell
dotnet build
```

Run `dotnet test` for metadata, composition and selected feature tests. WPF tests use STA and isolated host data under their output directory.

Copy local.props.example to the ignored local.props and set NinaExecutable to your NINA installation. Close NINA before running the DeployPlugin target on the project under src. Normal builds do not deploy.

## Selected examples

This project starts with the manifest and lifecycle skeleton. Generate a scratch project with feature switches to try the examples.

## Package

Set your real title, author, description and repository in Plugin.props. Add plugin-owned dependencies and assets to PluginPackageFile in build/Plugin.targets. Do not include NINA assemblies.

```powershell
./build/Build-Release.ps1 -Tag v1.0.0.0 -Repository you/your-plugin
```

This builds, tests and packages without uploading. ZIP and manifest files go to artifacts/release. Plugin versions have four numeric components.


## Learn more

Read the [NINA plugin developer handbook](https://github.com/isbeorn/nina.plugin.template/blob/master/docs/README.md), particularly [publishing](https://github.com/isbeorn/nina.plugin.template/blob/master/docs/publishing.md), [testing/debugging](https://github.com/isbeorn/nina.plugin.template/blob/master/docs/testing-and-debugging.md) and [adding examples later](https://github.com/isbeorn/nina.plugin.template/blob/master/docs/getting-started.md#add-another-example-later).
