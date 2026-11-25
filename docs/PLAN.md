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

# Updating from a branch inside N.I.N.A.

Done 2026-11-07. The locked-DLL problem (`DeployPlugin` cannot replace a loaded assembly) is sidestepped the way
N.I.N.A.'s own plugin manager does it: `PluginLoader.DeployFromStaging` moves whatever is in
`%LOCALAPPDATA%\NINA\PluginStaging\<version>\` into `Plugins\<version>\` at start-up, before loading. The plugin
builds into that folder and never touches its own files.

- `build/Plugin.targets`: `ObsGitStamp` (commit, branch, date, dirty as `AssemblyMetadata`, informational version
  `1.0.0.0+<branch>.<short>[.dirty]`; `IncludeSourceRevisionInInformationalVersion` off) and `StageForNina`
  (plain MSBuild `Copy` of `@(PluginPackageFile)` into `-p:NinaStageDirectory`, plus the `.obsservices-files` manifest;
  no PowerShell).
- `src/ObsServices.Core/Update/`: `CommandRunner` (git/dotnet out of process, streamed log, kill on cancel,
  `GIT_TERMINAL_PROMPT=0`), `GitRepository` (clone/fetch/sync; managed = `checkout -B` + `reset --hard`, unmanaged =
  refuse dirty, switch only when clean, `merge --ff-only`), `PluginUpdater` (open/clone, check = behind count, update =
  sync + build + verify manifest; clears a previous stage only when the manifest proves it is ours), `InstalledBuild`
  (reads the stamp), `StaleFiles` (manifest vs. assemblies in the plugin folder).
- `src/ObsServicesPlugin/Nina/NinaUpdater.cs`: paths (stage = `Constants.StagingFolder` + the folder the plugin was
  loaded from, else the sanitized title as Deploy-Plugin.ps1 makes it; managed clone =
  `%LOCALAPPDATA%\NINA\ObsServices\src`), Check/Update/Cancel/Restart commands, status and a 40-line log, the restart
  (profile release, start `Environment.ProcessPath`, shutdown; refused while a sequence runs; asks first), and the
  stale sweep into `Constants.DeletionFolder` at `Initialize`.
- Settings: `UpdateRepositoryUrl`, `UpdateBranch` (dev), `UpdateSourceDirectory` (empty = managed clone),
  `CheckUpdatesOnConnect` (fetch only). Options page: the "Update from branch" section.
- Options page rule: N.I.N.A.'s CheckBox template is an ON/OFF switch with no content presenter, so a CheckBox's
  `Content` never shows; every switch has a label in the left column and a hint TextBlock beside it.
- Tests: `UpdateTests` (core, Linux: local bare remote, managed clone follows and resets, user checkout refused when
  dirty or diverged and fast-forwarded otherwise, branch switch, real `dotnet build` of a stand-in project with a
  `StageForNina` target, stale files, stage ownership), `NinaUpdaterTests` (folder-name rules).

First things to try on Windows: Check (expect "This is the build N.I.N.A. is running" once a stamped build is
installed), Update on a branch with a new commit, confirm `%LOCALAPPDATA%\NINA\PluginStaging\3.0.0\Obs Services
Test Plugin\` fills, restart, confirm the Running line shows the new commit and the staging folder is gone.

Not done: rolling back to a previous build (pick an older commit by hand for now), a remote trigger through the
`sequencer` entity, CI-built packages instead of a local SDK.

# Observation requests: the task queue (2026-11-24)

The service now turns a started request into short, frequent `ObservationTask`s (one slew + one run of exposures,
two passes queued ahead per instrument; protocol still v6). `SiteAgent` therefore keeps an explicit queue:

- Tasks are queued in arrival order and run strictly one at a time (`current` + `queue`); `plan` reorders the
  not-yet-started ones (named ids first, the rest keep their relative order), as the virtual agent does.
- `running` is published when a task actually starts, not at receipt (before, every assigned task was reported
  running at once and serialised only by `ObservatoryAgent`'s instrument lock).
- At start, after `AcceptTask`: `constraints.latest` in the past → `skipped`, message `its window had closed`
  (the exact words of `virtual_agent.rs` and `python/observatory/agent.py`). Never aborts a running task;
  `earliest` and `min_altitude_deg` are not gated.
- `task_cancel` of a queued task drops it and reports `cancelled` at once; stop / sequencer idle / agent stop
  drop the queue as well (`cancelled` / `agent stopped`), matching the virtual agent's `stop`.
- `RunningTasks` is now the running task only (one at most); `QueuedTasks` lists the rest in start order.
- Test: `AgentIntegrationTests.TasksRunOneAtATimeInOrderAndAClosedWindowIsSkipped` (four tasks in a second, a
  `plan` through `POST /api/sites/{id}/plan`, never two `running`, exposures in planned order, late task skipped
  with no slew).

Not done, pending the owner's word: a filter the wheel does not have still logs a warning and exposes through the
current filter (`ObservatoryAgent.SelectFilterAsync`); the service's proposal is to fail the task instead. Stacked
filter wheels (`Exposure.Filters` with several wheels) stay as they are.

# The 2026-11-24 blackout: link hardening

Server-side analysis of the 02:14–03:02 UTC run: telemetry arrived once every ~61 s in stretches of minutes,
frame announcements and task completions were lost with it, and tasks whose completion was lost were handed over
again and run again (79 frames taken, 37 received, doubled exposures). Plugin-side reading of the code found a
mechanism that produces exactly that signature whatever stalls the socket: the publish loop was sample → send →
sleep, and a send had no timeout, so a stalled socket held the loop until the keep-alive watchdog (20 s ping +
30 s pong) aborted it; one sample per reconnect cycle, and the missing seconds were never sampled, so no replay
could fill them. Root cause of the stall not established (server deploy at 02:12 vs. uploads saturating the
uplink: the first night with frame sharing on, 68 frames in request 2, no rate cap).

Changes in `SiteAgent`:
- Outbox + sender: `SendAsync` only enqueues (cap `BacklogLimit`); `SendLoopAsync` drains it per session, dequeuing
  a message only once it is on the wire; after a session `PruneOutbox` keeps StateMsg/Event/ImageCaptured. Hello is
  sent directly first. `SendTimeout` (15 s) aborts a stalled socket and ends the session with
  "the socket accepted nothing for 15s (stalled link)". `Pending` = outbox size.
- `Remember`/`FinishedTask`: the last `FinishedMemory` (500) outcomes; a `task_assign` for a remembered id
  re-publishes the outcome instead of running (logged "handed over again").
- Uploads: `UploadRateCapBps` (plugin setting *Upload cap*, 8 Mbit/s default, split across the grant's lanes, min
  with the grant's `rate_bps`) and `UploadGateAsync` before every chunk (socket up and `UploadSettleDelay` = 10 s
  settled). `FrameUploader.UploadAsync` gained a `beforeChunk` gate. Upload start is logged with bytes and cap.
- Logging: session connected (with replay count), closed by the service / ended with reason, both with duration.

To confirm the cause next run: N.I.N.A. log lines `agent socket: session ended after …: …` (a stalled-link reason
means our side detected the stall; spacing shows the cycle), the spool's `spool.json` on the PC for frames never
announced, and on the server the image copies' `received` times against the bad stretches.

Follow-up (same day): the uploader was neither a stack nor a queue. The server grants one hash per announcement with
`max_concurrency` 2, and the agent ran every grant as its own two-lane pool, so N announcements meant up to N uploads
in flight (68 during request 2) and the new cap was split per grant, bounding nothing site-wide. Now one scheduler
(`pendingUploads` / `inFlight` / `PumpUploads` / `RetuneRates` in `SiteAgent`): every granted hash joins one queue,
ordered by the spool entry's `CapturedAt`, drained by `UploadConcurrency` (2) lanes, never more than a grant's own
`max_concurrency`; the cap is divided over what is in flight and retuned as lanes fill and empty (each lane holds
its own grant copy, whose `rate_bps` the uploader re-reads per chunk). A later grant for a queued hash refreshes its
token; one for a hash in flight is ignored. `UploadQueue` lists what waits. Test: `UploadSchedulerTests` (six frames
spooled newest first, six single-hash grants, a slow fake upload server: never more than two at once, both lanes
used, the frames that waited went oldest first).

# 2026-11-25: outbox lanes and the learned upload rate

Night of the 24th/25th: N.I.N.A. logged growing batches of "task … was handed over again; it already ended
completed" at each reconnect. The server applies a task status from any state message (no staleness check), so the
completions were simply not arriving: the single-FIFO outbox sent the oldest telemetry first and a stalled link that
takes a few messages per session never reached them; each reconnect appended more re-published outcomes.

- Outbox lanes (`urgent` list, `latestTelemetry`, `telemetry` queue; `PeekOutbound`/`DequeueOutbound`): everything
  but periodic samples goes first; then the newest sample; then the backlog oldest first. `SendOutcome` keeps one
  pending outcome per task (`pendingOutcomes`). `OutboxTests` checks the order and the dedupe.
- `Imaging/UploadRateController`: AIMD on the upload rate. Initial 2 Mbit/s, floor 256 kbit/s, max 1 Gbit/s,
  `Ceiling` = the user's cap. `OnSend(took)` ≥ 1.5 s → halve (one decrease per 10 s); `OnStall` → halve + 3 min
  hold; `Tick` once a second: a quiet 30 s window while uploading → ×1.1, or achieved < half the rate → follow
  achieved ×1.25. Wired in `SiteAgent`: sends are timed in `SendRawAsync` (a slow one is logged as "congested
  link"), the stall path calls `OnStall`, the publish loop ticks it with `UploadRateBps(30)`, `Changed` retunes the
  lanes (`EffectiveUploadRateBps`, `UploadRateAuto`, `UploadRateCapBps` = ceiling). Lane `max_chunk` ≈ 2 s at the
  rate, never above the grant's own (the retune first ignored the grant's `max_chunk`; the scheduler test caught
  it). `FrameUploader` re-reads `grant.MaxChunk` per chunk.
- Plugin: `UploadRateAuto` (on), `UploadRateMbps` now a ceiling (default 0 = none), `LearnedUploadRateBps`
  persisted from `UploadRate.Changed` and restored at connect; the spool status shows "uploads at ≤X Mbit/s
  (learned)". Options: the Upload rate row is a switch plus the ceiling field.
- If stalls continue while uploads sit at the floor, bandwidth is ruled out as the cause of the blackout.

# 2026-11-25 (later): the spool desync

Symptom: frames held in the spool, nothing outstanding on the server, no uploads, bandwidth available, after several
restarts. Cause: the server's `inventory` handler only grants frames it has a record for; frames whose
`image_captured` was lost (the blackout, or the in-memory outbox at a N.I.N.A. restart) are counted in a warning event
("spool inventory lists N frames this service never saw announced; they are not fetched") and ignored. Nothing on the
agent re-announced them.

- `FrameSpool.Insert` stores the announcement as `<hash>.json`; `Announcement(hash)` returns it, or rebuilds one from
  the file (kind/camera/task/captured_at from the index, EXPTIME/FILTER/GAIN/OFFSET/XBINNING/CCD-TEMP from the header);
  `Release` deletes the json too.
- `SiteAgent.ResyncSpoolAsync`: after welcome (with the inventory) and every `SpoolResyncInterval` (10 min) while
  connected, announce again up to `SpoolResyncBatch` (50) pending frames with no grant pending and no upload under
  way, oldest first; a frame that cannot be announced (file gone) is dropped from the spool with a warning.
- Double grants (inventory + re-announce) made the server replace the token mid-upload → 401 → the upload gave up.
  Now `RunGrantAsync` switches an in-flight lane to the newer token/url/expiry, and a 401 refusal is retried (the
  lane has the newer token by then) up to `UploadAttempts`.
- Test: `ImageTests.AFrameLeftInTheSpoolIsFetchedThroughTheInventoryOfTheNextAgent` now also spools two frames the
  service never saw, one without its stored announcement; both are fetched by the next agent, the rebuilt one with
  kind and exposure from the header, and the server's "never saw announced" event proves the inventory alone would
  not have done it. `UploadSchedulerTests` ordering check is per pair of lanes (two lanes freeing together).
