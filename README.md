# ObsServicesPlugin

**Branch `nina-3.2`: the build for N.I.N.A. 3.2.0.9001 (stable), on .NET 8.** Feature work happens on `dev`
(N.I.N.A. 3.3 nightlies, .NET 10); this branch carries the same code with only configuration differences:
`Directory.Build.props` (frameworks and the NINA package version), `global.json`, the solution file (.sln here,
.slnx on dev), `Plugin.props`
(`MinimumNinaVersion`), the MVVM toolkit version N.I.N.A. 3.2 ships, and the composition test, which does not
export the symbol broker that only exists in 3.3. Merge `dev` into this branch to bring changes across; conflicts
should only ever be in those files.

A plugin for NINA 3.2.0.9001 or later, targeting .NET 8. Metadata lives in Plugin.props. The plugin GUID is permanent.

It makes a N.I.N.A. installation a **site agent** of the telescope_control service (observatory.services): the
plugin connects outbound to `/ws/agent/{site}`, publishes mount, camera and roof state to the site's entities,
and carries out the observation tasks the service assigns through N.I.N.A.'s own equipment mediators.

## Layout

```
src/ObsServices.Core          net10.0, no N.I.N.A. or WPF: the agent protocol (Protocol/), channel names and
                              derived entity ids (Channels.cs), sidereal-time helpers (Astro.cs), the agent
                              runtime (Agent/SiteAgent.cs: socket, reconnect, backlog replay, state cadence,
                              task dispatch, command acks, the frame spool and its uploads), the device interfaces
                              a host implements (Devices/), the generic agent that drives them
                              (Agent/ObservatoryAgent.cs: manifest binding, telemetry mapping, task execution,
                              commands, frame announcements) and Imaging/ (FITS decoding, the pixel hash, statistics,
                              previews, the on-disk spool, the resumable uploader). Builds and tests anywhere.
src/ObsServicesPlugin         net10.0-windows: the N.I.N.A. plugin. Plugin.cs is the manifest and agent host (settings,
                              connect/disconnect, options page in Options.xaml); Nina/ implements the core's device
                              interfaces over N.I.N.A.'s mediators (mount, camera + image save pipeline, filter wheel,
                              dome shutter as roof, focuser). Runs on Windows only.
tests/ObsServices.Core.Tests  protocol/astro unit tests plus an integration test that starts the real
                              telescope_control server and drives a software site through the protocol.
tests/ObsServicesPlugin.Tests N.I.N.A.-hosted tests; need the Windows desktop runtime.
```

## How it fits together

1. In N.I.N.A., Options > Plugins > this plugin: enter the service URL, the site id and an agent token from the
   site's *Agent tokens* window, then Connect (or tick *Connect when N.I.N.A. starts*). Settings are per profile.
   The plugin starts in **telemetry only** mode: the service sees the rig but tasks come back as `skipped` and
   commands are refused in their ack. Untick it to allow remote observations and commands; the switch is live.
2. The plugin opens the agent socket. The service answers with the site's manifest; the agent binds the chosen
   instrument's mount and camera, the filter wheel and focuser mated under that mount, and the first roof.
3. Every state interval it publishes what N.I.N.A.'s mediators report: the mount's axis angles (the twin's
   joints: hour angle and declination, or on a German equatorial the flipped pose past the pole when N.I.N.A.
   reports the tube west of the pier), `pier_side`, mode, sky RA and Dec, camera state and temperature, filter,
   focus position, shutter state.
4. Tasks the service assigns run through N.I.N.A.: open the shutter, unpark, slew (J2000 in, N.I.N.A. transforms),
   change filter, expose through the imaging mediator and the normal image save pipeline, report frames and
   progress on the task entity. Cancel aborts the exposure. Commands stop, park, unpark, open/close roof and
   safe map to the matching mediator calls; `set_time_scale` is acked as unsupported. Tasks that carry a
   program run through N.I.N.A.'s sequencer instead (next section).

## Programs and the sequencer entity (protocol v4)

The service can hand N.I.N.A. one of its **programs** instead of a declarative task: a sequence, target or
template file uploaded to the organisation (`/api/orgs/{org}/programs`) and queued on the site. The plugin
announces `program:nina.sequence`, `program:nina.target` and `program:nina.template`; a target or template is
injected into the advanced sequencer's target area, a whole sequence replaces the loaded one (off until *Allow
whole-sequence programs* is ticked). Add a **`sequencer`** component to the site's assembly and the plugin
publishes N.I.N.A.'s state to it: software, version, profile, idle/running, the loaded program, the running
item, installed plugin assemblies, and the telemetry-only switch, which the service can flip through a `set`.
`set` also slews the mount (`ra_deg`/`dec_deg`), parks or stops it (`mode`), changes filter and focus, and
opens or closes the roof; the agent declares exactly those as writable. See `docs/PLAN.md`.

## Frames (protocol v6)

With *Share the frames N.I.N.A. saves with the service* ticked, every frame N.I.N.A. saves is **announced** to the
service: its pixel hash (SHA-256 of the pixels as 32-bit floats, the same name the service and Fits_Utils use),
dimensions, kind, exposure, filter, gain, offset, binning, sensor temperature, the FITS header, N.I.N.A.'s
statistics and star count, and a small PNG preview. Never the pixels: the service **grants** uploads when it wants
a frame, and the plugin moves it over HTTP in resumable 8 MiB chunks, two at a time, retrying through network
trouble and resuming at the committed offset after a restart. Once the service holds the frame it **releases** it
and the plugin drops its copy. Lights are shared by default; calibration frames and snapshots have their own
switches. The plugin writes its own plain FITS copy into a **spool** folder (default
`%LOCALAPPDATA%\NINA\ObsServices\spool`) with N.I.N.A.'s FITS writer, so the user's XISF, TIFF or compressed output
is untouched and irrelevant; the spool is capped (20 GB by default; only frames the service already received are
evicted) and survives restarts: the inventory sent after every reconnect lets the service fetch what it missed.
Add an **`uploader`** component to the site's assembly and the plugin publishes the spool to it: frames and bytes
waiting, free disk, upload rate, age of the oldest frame. Frames taken while a task runs are tagged with it; frames
taken by hand are announced too. Sharing is off by default and independent of telemetry-only mode: frames only ever
leave the machine, so the mode does not gate them.

## When the service is unreachable

The agent retries forever: a failed or dropped connection is retried after 1 s, doubling to a cap of 60 s, and a
successful connect resets the pause. A handshake that stalls is abandoned after 15 s; an unanswered WebSocket ping
closes a half-open connection after 30 s. The reason for the last failure, including the HTTP status of a refused
token, is in the plugin's status line and N.I.N.A.'s log. State keeps being sampled through an outage and up to
20,000 messages are replayed with their original timestamps once the service is back, so the site's history has no
gap. Running tasks continue locally and report their outcome on reconnect.

## Develop on Linux

N.I.N.A. is WPF and does not run on Linux, but every project here compiles on Linux and the core tests run
there. `scripts/build.sh` wraps the official .NET 10 SDK container, so nothing needs installing beyond Docker:

```bash
scripts/build.sh build        # restore + build all four projects
scripts/build.sh test         # core unit tests + the integration test against telescope_control
scripts/build.sh shell        # a shell with the repo at /src
```

The integration test looks for `../telescope_control/target/debug/telescope-server` and `../telescope_control/library`
(override with `TELESCOPE_CONTROL`, or `TELESCOPE_SERVER_BIN` / `TELESCOPE_LIBRARY` inside the container) and skips
with a reason when they are missing. Build the server once with `cargo build -p telescope-server` in that checkout.
The N.I.N.A.-hosted tests under `tests/ObsServicesPlugin.Tests` compile here but only run on Windows.

## Develop on Windows

Install the .NET 8.0.4xx SDK and PowerShell 7 on Windows. Open ObsServicesPlugin.sln in Visual Studio 2022 or use the CLI (this branch uses the classic .sln because the
.NET 8 SDK does not read .slnx).

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
