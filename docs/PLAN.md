# Implementation plan: programs, the sequencer entity and protocol v4

Status as of 2026-11-04. Phases 1 to 4 are done and tested on Linux; 5 to 7 are written and compile but have
not run against a live N.I.N.A. yet.

## What the service provides (telescope_control, protocol v4)

* **Programs** (`/api/orgs/{org}/programs`): opaque bodies in a named format (`nina.sequence`, `nina.target`,
  `nina.template`, …) stored per organisation with a digest (targets, exposures, required extensions). Queued on
  a site as a task carrying a `program` reference; the body is inlined in `task_assign`.
* **Capabilities**: an agent lists `program:<format>` in `hello`; a program task is only ever assigned to such
  an agent and otherwise waits ("waiting for an agent that runs nina.sequence programs").
* **The `sequencer` component**: the controller entity. A site owner adds one to the assembly; the agent binds
  to it and publishes `software`, `version`, `profile`, `state`, `program`, `running_item`, `instrument`,
  `extensions`, `telemetry_only`.
* **`set` commands** on writable entity properties, answered with `accepted` / `done` (+ `observed`) /
  `rejected` / `unbound` / `unsupported` / `busy`. The library marks nothing writable: the agent declares what
  it can set in an `entities` message after the manifest.

## Phases

1. **Protocol v4 in the core** — done. `ProgramRef`, `Exposure.Filters`, `PropertyInfo.Writable`, the
   `entities` message, `set` decoding with `SetReply`, `SetDoneAsync`/`SetFailedAsync`, `RunProgramAsync`
   with the capability gate, `NotSupportedException` → `skipped`. Wire tests mirror `wire.rs`.
2. **Sequencer host interface** — done. `ISequencerHost` (reading + `RunProgramAsync` per format);
   `SiteBinding.SequencerEntity`; `ObservatoryAgent` declares the host's formats as capabilities, publishes
   the sequencer properties, runs program tasks under the one-instrument lock with ordered, awaited frame
   progress (a late report can no longer flip a completed task back to running).
3. **Sets in the generic agent** — done. Mount `ra_deg`/`dec_deg` (accepted → done with observed axes),
   `mode` parked/stopped/tracking, wheel `filter`, focuser `focus`, roof travel joint, sequencer
   `telemetry_only` and `state = idle`. Busy while a task holds the instrument; telemetry-only rejects all but
   its own switch. After the manifest the agent declares the writable set and the wheel's carousel.
4. **Live-server tests** — done (`tests/ObsServices.Core.Tests`, 27 green): a site built inline from the
   observatory assembly plus a `sequencer`; the M57 reference sequence uploaded, queued, left waiting for a
   plain agent, run by a capable one with frames counted; sets for slew, mode, focus and roof; the
   sequencer's own controls; telemetry-only through a set.
5. **The N.I.N.A. sequencer host** — written (`src/ObsServicesPlugin/Nina/NinaSequencerHost.cs`), untested on
   Windows. `nina.sequence` replaces the loaded sequence and starts it (opt-in), `nina.target` is injected into
   the target area, `nina.template` is wrapped in a target container with the task's coordinates. Cancel maps
   to cancelling the sequence or interrupting the container. Frames come from `ImageSaved`.
6. **Deserialisation** — the sequencer factory is reached through the mediator's private `sequenceNavigation`
   field (same name in 3.2.0.9001 and 3.3). `NinaSequencerHostTests` asserts the field exists. Upstream ask:
   `ISequenceMediator.DeserializeContainer(string json)`, after which `TryGetFactory` goes away.
7. **Settings and page** — written. `Sequencer` (which instance), `AllowWholeSequences`, announced formats.

## First things to run on Windows

1. `dotnet test tests/ObsServicesPlugin.Tests` (never executed yet).
2. In N.I.N.A. with the simulators: connect, confirm the `sequencer` entity reports; upload a target container
   as a program and queue it; then a whole sequence with the option enabled; then `set` a slew from the
   entities window.
3. Confirm `InstalledExtensions()` lists the plugin assemblies the digest names (it reads loaded assemblies
   under N.I.N.A.'s plugins folder).

## Known gaps

* Program frames count light frames whose target name matches the task's; a program that renames its target
  mid-way under-counts. Progress is advisory; completion comes from the sequencer.
* A `nina.sequence` run returns when the whole sequence (start, targets, end) is over. If the user's end area
  parks and disconnects, that is what happens.
* Per-wheel `filters` on exposures: the device runner honours the entry for its bound wheel and leaves others
  alone; the N.I.N.A. filter wheel is one wheel.
* The 3.2 branch has the same code; only configuration differs. Merge `dev` into `nina-3.2` after each phase.
