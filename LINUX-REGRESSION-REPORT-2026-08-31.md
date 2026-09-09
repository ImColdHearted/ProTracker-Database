# ProTracker Database - Linux regression investigation (tester "Lugario"), 2026-08-31

Status in one line: the root cause on the tester's machine is **not yet confirmed**. The evidence available (one resized Discord screenshot) narrows the failure to one region of the pipeline but cannot pick the stage. One real defect that the evidence does support - the status line stayed on "Tracking started." through five distinguishable failure states - is fixed (MIGRATION_GUIDE.md §134), and the next report from the tester will carry the data this one lacked.

## 1. What the tester experienced, and how it presents

The tester's message was "I am sorry, but it is still not working." with a screenshot. Read at 4x, the screenshot shows:

- The tracker window running normally: menu bar, `Set Target`, a `Stop` button (tracking was started), `Reset`, the pin, the `Exclude Stats` button (§126), black header chips over a single-row encounter table (§125/§132). Those UI elements identify the build as the **8/30 release**, not 8/27.
- Status line: exactly `Tracking started.` - Current Encounter card: `None` - encounter table: empty.
- Below it, the top part of a PRO client (window title `PROClient`) with the battle strip `Lugario VS. Wild Rhyhorn` visible: a wild battle open while the tracker counted nothing.

So "does not work" means, specifically: the app launches, a client is bound, tracking starts, and no encounter is ever registered while wild battles are on screen. It is not a launch failure, not a missing-client message, not a crash, and not an error banner. Nothing about monitor resolution, GUI scale or OCR pixels was inferred from the resized image.

## 2. Release comparison (last known good vs first failing)

| | Last known good | First failing (per tester) | Current |
|---|---|---|---|
| Forum date | 8/24/2026 (second update) | 8/27/2026 | 8/30/2026 (plus 8/31 rebuild) |
| Forum notes | "Fixed shiny/event form detection again. Linux added back in after a weekend of test. Simplified the setup process" | "Added soft lock to client ... PVP Tracking is available again. Added to appearance ... Added to exclude stats ... Added 2 sounds (Windows only)" | "Fixed Linux ... Redid encounter tables" |
| Guide sections | up to about §56 | §57-§78 (Catch Logs §57/§76/§77, PVP detection §58, client soft lock §60, hide stats §62, sounds §65/§71/§72, Text Stroke §78) | §116/§118 Linux setup, §125-§132 tables, §133 fonts |
| Intermediate release | - | 8/26 ("4 clients, separate settings", PVP flagging bug) - not known whether the tester ran it | - |
| App version string | 1.0.0.0 | 1.0.0.0 | 1.0.0.0 (unchanged across all releases - see item 11) |
| RID / runtime | linux-x64, self-contained, .NET 10.0.11 | same | same |
| OCR / imaging packages | TesseractOCR 5.5.2, SkiaSharp 4.151.1 (+NativeAssets.Linux), Avalonia 12.1.1 | same (no package changes in the window) | same |
| Linux scripts in zip | `linux-start.sh` (hand-added, top level) + `linux-setup.sh` + `linux-fix-tesseract-ocr.sh` (csproj Content) | same set intended; the user later noticed one `.sh` had gone missing from a zip at some point | `linux-setup.sh` + `linux-fix-tesseract-ocr.sh` in `publish/linux-x64`; `linux-start.sh` must still be copied in by hand |
| Package checksum | not available (zip is on MediaFire only, not on disk) | not available | pre-§134 publish of 2026-08-31 03:31 UTC: `ProTrackerDatabase.Avalonia.dll` SHA-256 `bcb0b5f787183b4d15ba577925f155578c1cebdef647e4ac9f3d19baa858e123`, launcher `85fedbaae2b53dcf2b78e25fbf03ded617be3ea4705c71f19cc20107b337cf9a` |

The Linux capture path itself - `LinuxX11WindowCaptureService` (wmctrl + import), `ProcessRunner`, `WindowCaptureServiceFactory` - did not change between 8/24 and 8/27 (file dates 8/20 and 8/24; §117 on 8/30 changed a log sentence only). The battle-window locator changed only in §119/§120 (8/30). The tracking loop's shape (capture, corner OCR, locate, identify) is the same in the 8/26 source snapshot kept from §57 as it is today.

## 3. First bad commit / commit range

Not producible. The project folder has no `.git`; the `.git` in the home directory is an empty `git init` (only `hooks`, `info`, `objects`, `refs`; no `logs`, so it never took a commit). The 8/24 and 8/27 packages are not on disk. The MIGRATION_GUIDE section ranges in item 2 are the only change log, and each section in the 8/27 range was read against the Linux path: none touches capture, window enumeration, or the locator.

## 4. Environment comparison

| | Developer VM (works) | Tester (fails) |
|---|---|---|
| Distro / version | Ubuntu 24.04.4 LTS | unknown |
| Desktop / session | GNOME, `XDG_SESSION_TYPE=wayland`, `WAYLAND_DISPLAY` set (XWayland capture proven: 161/255) | unknown |
| Graphics | VirtualBox, software rendering | unknown (a real GPU and compositor is the most likely difference) |
| PRO client | native Linux client, one window, 1152x768 and 1024x640 tested, GUI scale 1 and 2 | native client per the `PROClient` title; window size, count and GUI scale unknown; the visible window appears to extend below the screenshot's bottom edge |
| Display scaling | 100% | unknown |
| Install path / setup | `~/Desktop/ProTrackerDatabase-LinuxX64/linux-x64/`, `linux-setup.sh` run | unknown whether `linux-setup.sh` or `linux-start.sh` was run; OCR natives evidently load (see item 5) |
| Number of PRO clients | 1 | unknown |
| Tracker build | 8/30 | 8/30 |

## 5. Earliest failing stage (18-stage trace)

"Verified" below means from the screenshot or from the code paths that would otherwise have changed the status line. The tester's screenshot is assumed to be more than about a minute into the session; if it was taken seconds after pressing Start, stages 8-14 revert to "not yet observed".

| # | Stage | Developer VM | Tester |
|---|---|---|---|
| 1 | Process launches, main window shows | working | working (screenshot) |
| 2 | Profile / appearance / UI prefs load | working | working (custom background, table prefs visible) |
| 3 | Capture backend chosen; wmctrl and import/maim found | working | working (inferred: a missing tool puts its message on the status line at Play) |
| 4 | `PROClient` window enumerated (`wmctrl -l -p` + `/proc/<pid>/comm`) | working | working (inferred: "No Pokémon Revolution Online client was found" would otherwise show) |
| 5 | Client slot bound, session lock free | working | working (inferred: lock conflicts show a message and Play does not proceed) |
| 6 | Capture bound to a window handle | working | working (inferred) |
| 7 | Start pressed, loop running | working | working ("Tracking started.", Stop button) |
| 8 | `import -window` returns bytes | working | working (inferred: a null capture writes "Waiting for PROClient..." on the next tick) |
| 9 | PNG decodes | working | working (inferred: same path as 8) |
| 10 | Frame is non-blank | working (60-161/255) | **not yet observed** (before §134 nothing on screen distinguished it) |
| 11 | OCR engine loads (natives present) | working | working (inferred: the corner OCR runs on the first frame and a missing native puts §115's message on the line) |
| 12 | Map-name corner reads | working | not yet observed |
| 13 | Battle window located | working (7+9 frames, 3 resolutions) | **not yet observed - prime suspect** |
| 14 | Title OCR contains "VS" | working | **not yet observed - second suspect** |
| 15 | Species matched | working | failing (nothing was ever identified) |
| 16 | Encounter registered in session and UI | working | failing (Current Encounter `None`, empty table) |
| 17 | Persistence (session file, encounter DB) | working | not reached |
| 18 | Status line reflects the real state | working | **failing - fixed in §134** ("Tracking started." through the whole session) |

Earliest failing stage: between 10 and 14. The frames arrive and decode and OCR runs, but either the frames are blank, or no battle window is located in them, or one is located and its title never reads. Reasons that fit and are not distinguishable from here: a black or stale frame from a compositor/driver for a GL window; the wrong one of two PRO windows bound; a PRO window partly off-screen; a frame the locator rejects for size. A boss/PVP stand-down was considered and ruled unlikely: a wild title cannot pass `PvpBattleDetector` (needs the green indicator bar) or `BossBattleDetector` (needs a boss name).

## 6. Evidence

- Tester screenshot (Discord, 342x424 thumbnail, upscaled for reading; nothing geometric inferred).
- Forum topic 278386 changelog: entries for 8/24 (two), 8/26, 8/27, 8/29, 8/30, and the Linux instructions still reading `bash linux-start.sh`.
- Source read: `Tracking/EncounterTracking.cs` (loop, every status write), `Tracking/PvpTracker.cs`, `Tracking/BossCooldownTracker.cs`, `Tracking/BossPvpDetectors.cs` (PVP gating), `Tracking/Capture/LinuxX11WindowCaptureService.cs`, `Tracking/Capture/ProcessRunner.cs`, `Services/ClientWindowAssignmentService.cs`, `ViewModels/MainWindowViewModel.cs` (auto-assign, Play, watchdog), `Tracking/TrackerDiagnostics.cs`, `Views/MainWindow.axaml.cs` (Report a Problem).
- 8/26 source snapshot of `EncounterTracking.cs`, `MainWindowViewModel.cs`, `BattleWindowLocator.cs` (kept from §57) diffed against today's files.
- Developer VM logs and PRO frames from 8/30 (seven-frame and DPI bundles): `Capture 1024x640, mean brightness 81/255 - ok`, RouteDetector OCR lines every 5 s, encounters registered.
- Device listing: no `.git` in the project; empty `.git` in the home directory; publish folders rebuilt 2026-08-31 03:31 UTC.

## 7. Files changed (§134)

- `Tracking/EncounterTracking.cs` - idle-status rules: blank / undersized frame reported at once; located-but-unreadable battle window reported after 10 s; quiet frames described after 15 s idle; stand-down reported; every text emitted only on change and also logged.
- `Tracking/TrackerDiagnostics.cs` - `RecordBattleLocate` and the locate counts on the existing capture heartbeat line; `RunTrackingCheck()` (one-shot pipeline check written to the log); §114's blank-frame verdict no longer blames Wayland (§117).
- `Tracking/PixelDetectors.cs` - `MinBattleWidth` and `TitleBarProbeRows` made `internal` (no logic change; locator code otherwise byte-identical).
- `Views/MainWindow.axaml.cs` - Report a Problem runs the tracking check off the UI thread before copying the log and saves it as `ProTracker-Report-<timestamp>-TrackingCheck.txt` (four files instead of three).
- `MIGRATION_GUIDE.md` - §134.

Nothing here reads or writes the tester's data differently: no session, catch log, database, or settings file is touched, and the check is read-only.

## 8. Regression test results

- Structural battery (`/tmp/bat134.py`): 81 checks, 81 pass - byte conventions, brace/paren/bracket balance on all four files (string-aware stripper), placement of every new call, pipeline order of the check's stages, no data API referenced by the check, locator otherwise unchanged.
- Behavioural fixture (`/tmp/fix134.py`): a Python port of the new status state machine through nine timed scenarios, 16 assertions, 16 pass (healthy frames: one line at 15 s then silence; blank frames: one line at 0 s; 1x1 frames; blank then recovered; a normal encounter with a 1 s unreadable intro produces no false report; unreadable for 12 s: one line at 10 s; stale PVP flag: one line at 15 s and the watching line the moment it clears; null capture keeps "Waiting for PROClient..."; frame-size change re-describes once).
- Edit replayed from a fresh copy of the device files: identical. Committed with mtime guards; re-staged and compared: identical.
- Not compiled (no .NET SDK in this session). The first build is the real test, as for every section since §86.

## 9. Environments where it was tested

None at runtime. The only Linux configuration this project has ever exercised is the developer's Ubuntu 24.04 VirtualBox VM under GNOME Wayland with software rendering (8/30, pre-§134 build). "Linux is fixed" is not claimed anywhere.

## 10. Configurations not tested

The tester's machine; any X11 (Xorg) session; KDE, Xfce, Cinnamon, i3 or any non-GNOME desktop; a real GPU with NVIDIA/AMD/Intel drivers; a compositor other than Mutter; display scaling other than 100%; two or more PRO clients; a PRO window partly off-screen; a Windows PRO client under Wine; linux-arm64; macOS.

## 11. Package version and checksum

No package has been built from §134 yet. The app version string is `1.0.0.0` in every release so far (visible in every log's `Environment:` line), which is why a log cannot say which release produced it. Recommendation: bump `Version`/`InformationalVersion` in the `.csproj` per release (for example `3.0.20260831`) before the next publish, and record the zip's SHA-256 in the forum post. For reference, the pre-§134 linux-x64 publish of 2026-08-31 03:31 UTC hashes to `bcb0b5f7...858e123` (dll) and `85fedbaa...b337cf9a` (launcher); those will change with the rebuild.

## 12. Install instructions for the tester (data-preserving)

1. Extract the new zip to a fresh folder (or over the old one; either is fine).
2. In a terminal in the extracted folder: `bash linux-start.sh` if that file is there, otherwise `bash linux-setup.sh` from inside the `linux-x64` folder. Not as root, no permission changes.
3. Start the tracker as usual. Everything you have - hunts, counters, catch logs, settings, appearance - lives in `~/.local/share/ProTracker` and is not touched by the update. Do not delete that folder.
4. If the status line shows anything other than the usual "Tracking started." after a few seconds, it is now telling you what it sees; that sentence is the first thing to send.

## 13. Message for the tester (Discord)

> Thanks for the screenshot - it told me the app is running and tracking is on, but not what the tracker is seeing, and that is the part I need. Could you do one thing for me, it takes about a minute:
>
> 1. Get into any wild battle and, while the battle is still on screen, click **Report a Problem** in the tracker.
> 2. It saves three files to your `~/Downloads` folder named `ProTracker-Report-<date>...` (a `.png`, a `-PROClient.png` and a `.log`). Send me all three. (With the next update it will save a fourth, `-TrackingCheck.txt` - send that too if you have it.)
> 3. And three quick facts: which distro and desktop you use (e.g. "Fedora 40, KDE"), what `echo $XDG_SESSION_TYPE` prints in a terminal, and whether you had more than one PRO client open.
>
> Nothing in your hunt data or settings needs to change, please don't delete anything - the files above are exactly what shows me where it stops.

## 14. Remaining uncertainty

- The root cause on the tester's machine is not identified. The screenshot places it between "frame captured" and "battle window read", and the Report a Problem bundle is the smallest step that resolves it.
- The tester's belief that 8/24 worked and 8/27 did not is taken at face value; whether the 8/26 build was tried, and whether anything else changed on the tester's side between those dates (a distro or driver update, a second client, a moved window), is unknown.
- Whether the tester's 8/27 install had the OCR natives linked at all is unknown; on the 8/30 build they evidently load (item 5, stage 11), so the current failure is not that.
- §134 has not been compiled or run.
- Two side observations, not acted on: `linux-start.sh` silently changes two GNOME settings on the tester's desktop (`audible-bell`, `event-sounds`); and the forum's Linux instructions rely on `linux-start.sh`, which is not a `.csproj` content item and has to be copied into the zip by hand (§24) - the layout accident §116 was written to survive.

The identified defect is fixed and passes the listed tests. Confirmation on the affected user's machine is still pending.
