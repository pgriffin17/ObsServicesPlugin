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

# Frames: protocol v6

Status as of 2026-11-07. Phases 1 to 3 are done and tested on Linux; 4 and 5 are written and compile but have not
run against a live N.I.N.A. yet. The core also carries protocol v5's moving targets (`Target.Body`; an agent
without `target:<kind>` declines what the service did not translate).

## What the service provides (telescope_control, protocol v6; `docs/IMAGES.md` there)

* A frame is named by its **pixel hash**: SHA-256 over every pixel of the primary HDU as little-endian `f32`
  after `BZERO`/`BSCALE`, row-major. The header is not hashed. Shared test vectors in `tests/fixtures/pixel_hash/`.
* `image_captured` (announce: everything but the pixels, including a ≤ 64 kB PNG preview), `image_inventory`
  (after `welcome`), `image_grant` (upload these: base URL, bearer token, expiry, concurrency, chunk size, rate),
  `image_release` (the service holds them; the spool may drop them). Capability `images:spool`.
* Uploads over HTTP: `HEAD` for the committed offset, `PATCH` chunks with `Content-Range` (202 next, 409 resume,
  201 received and verified, 422 hash/sha mismatch or not a FITS image, 507 store full). Only an uncompressed
  primary HDU verifies: `.fits.fz`, XISF and TIFF are not accepted.
* An `uploader` library component carries spool telemetry: `queued_frames`, `queued_bytes`, `free_bytes`,
  `rate_bps`, `oldest_unsent_s`.

## Phases

1. **FITS and the pixel hash in the core** — done. `Imaging/Fits.cs`: header parser (the service's JSON rules),
   decoder for BITPIX 8/16/32/64/−32/−64, the hash (plus a streaming 16-bit shortcut that needs no float array),
   a writer for 16-bit frames, statistics (median, 1.4826 × MAD; histogram path for 16-bit), an asinh preview
   through a minimal PNG encoder (`Png.cs`). `PixelHashTests` runs all ten of the service's vectors.
2. **Spool, uploader and messages** — done. `Protocol/Images.cs` (v6 types; `ProtocolJson.Version = 6`),
   `Imaging/FrameSpool.cs` (directory + `spool.json` index, owned vs referenced files, cap, release deletion),
   `Imaging/FrameUploader.cs` (HEAD/PATCH, 409 resume, rate pacing), `Imaging/FrameDescriber.cs` (the
   announcement from a file or from a 16-bit array). `SiteAgent`: `Spool`, `AnnounceImageAsync`, the capability
   in hello, inventory after welcome, grants run `max_concurrency` at a time with 5 attempts and backoff,
   releases call `OnImageReleasedAsync` then drop, `uploader` telemetry merged into every state message,
   `image_captured` replayed after an outage. `ObservatoryAgent.AnnounceFrameAsync` fills camera, instrument,
   running task and wheel; a camera device entity is declared when the instrument's train ends on no sensor.
3. **Live-server tests** — done (`ImageTests`, 50 core tests green): a task's two frames announced, granted,
   uploaded, verified, read back byte for byte with preview and record, released and deleted; the `uploader`
   entity reports; a frame left in the spool by an agent that ignored its grant is fetched through the next
   agent's inventory. `FrameSpoolTests` covers persistence, release, the cap and a chunked upload with a 409
   against a fake HTTP server.
4. **The N.I.N.A. hook** — written (`Nina/NinaFrameSource.cs`), untested on Windows. `BeforeImageSaved` hands
   over the raw 16-bit array and metadata; a plain FITS is written with N.I.N.A.'s `FITS` class on a worker,
   N.I.N.A.'s statistics and star detection are awaited from the prepare task, and the frame is announced.
   32-bit cameras (`FlatArrayInt`) are skipped with one warning.
5. **Settings and page** — written. Share frames (off), calibration frames, snapshots, spool folder, cap, delete
   on release; a status line with what is waiting and the last error.

## First things to run on Windows

1. Tick *Share frames*, connect, take a snapshot with the simulator camera with *snapshots* enabled: the frame
   should appear under the site's Images panel within seconds, and the spool folder should empty.
2. Run a short sequence: frames carry the task id when the service assigned it, and `filters` names the wheel.
3. Check the preview against N.I.N.A.'s stretched view, and the header against the saved file's.
4. Pull the network cable mid-upload: the upload resumes at the committed offset after the reconnect.

## Known gaps

* 32-bit camera data is not shared (needs BITPIX 32 through N.I.N.A.'s cfitsio writer).
* Frames from a camera whose frames are not `ushort[]` (RAW DSLR data) are skipped the same way.
* The preview is computed from the raw pixels, not from N.I.N.A.'s debayered or stretched image: OSC frames
  preview as grey mosaics.
* The spool cap only evicts frames the service received; if the service withholds grants for long the spool
  grows to whatever the disk allows, and the `uploader` telemetry is the warning.
