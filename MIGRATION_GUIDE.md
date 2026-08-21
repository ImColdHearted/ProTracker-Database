# Pro Tracker & Database → Avalonia Migration Guide

Every WinForms form in the original project now has an Avalonia equivalent.
This document explains what changed, where behavior was intentionally
simplified or improved, and what to double-check once you restore/build in
Visual Studio (this was written without a compiler in the loop).

## 1. Window map (old → new)

| Old (WinForms) | New (Avalonia) |
|---|---|
| `ProTrackerandDatabase` (main form) | `ViewModels/MainWindowViewModel.cs` + `Views/MainWindow.axaml` |
| `ProTrackerandDatabaseCompactMode` | `Views/CompactWindow.axaml` - reuses `MainWindowViewModel` directly instead of duplicating display logic |
| `Forms/Appearance/AppearanceForm` | `ViewModels/AppearanceViewModel.cs` + `Views/AppearanceWindow.axaml` |
| `Forms/ClientSelector/ClientSelector` | `ViewModels/ClientSelectorViewModel.cs` + `Views/ClientSelectorWindow.axaml` |
| `Forms/Pokedex/PokemonSelectorForm` + `PokemonFormsPopup` | `ViewModels/PokemonSelectorViewModel.cs` + `Views/PokemonSelectorWindow.axaml` (forms/counterparts popup folded into an inline panel) |
| `Forms/BossTemplate/BossTemplate` + `BossPokemonCard` + `BossDifficulty` | `ViewModels/BossDetailViewModel.cs` (also defines `BossDifficulty`) + `Views/BossDetailWindow.axaml` (team-member popup folded inline) |
| Boss menu tree (`OpenBoss`/`BossDifficultyMenuItem_Click`, ~150 menu items) | `ViewModels/BossListViewModel.cs` + `Views/BossListWindow.axaml` - one browsable list + difficulty picker instead of a huge menu tree |
| `Forms/Cooldowns/Bosses/BossCooldownForm` | `ViewModels/BossCooldownViewModel.cs` + `Views/BossCooldownWindow.axaml` |
| `Forms/Counterparts/Counterparts` + `CounterpartHoverForm` + `DoubleBufferedFlowLayoutPanel` | `ViewModels/CounterpartsViewModel.cs` + `Views/CounterpartsWindow.axaml` (hover popup folded inline; double-buffering hack is unnecessary - Avalonia renders via Skia) |
| `Forms/Lifetime Stats/HuntingStats` | `ViewModels/HuntingStatsViewModel.cs` + `Views/HuntingStatsWindow.axaml` |
| `Forms/Lifetime Stats/PVPStats` | `Views/PvpStatsWindow.axaml` (static labels only - the original never wired this to a data source either) |
| `Forms/MegaStones/Test` (WebView2 guide viewer) | `ViewModels/GuideViewModel.cs` + `Views/GuideWindow.axaml` (uses `WebViewControl-Avalonia` instead of WebView2 - see §3) |
| `Forms/Interactive Maps/KantoMapForm` | `ViewModels/RegionMapViewModel.cs` + `Views/RegionMapWindow.axaml` (generalized to accept any region folder; map-pin overlay not ported - see §3) |
| `HoennMapForm`, `JohtoMapForm`, `SinnohMapForm`, `SeviiIslandMapForm1-3`, `SeviiIslandMapForm4-7`, `AstrellaMapForm`, `LegendaryPokemon`, `EVZones`, `ExcavationsForm` | `Views/PlaceholderWindow.axaml` - **these were empty in the original project too** (sized/titled Form, no code-behind logic beyond a background image panel). Confirmed by reading each `.Designer.cs`. |
| `Forms/Interactive Maps/InteractiveMaps` (`UserControl`) | Not ported - it was an empty, unused `UserControl` with no logic in the original. |
| MessageBox.Show(..., YesNo) call sites | `Views/ConfirmDialogWindow.axaml` - one reusable Yes/No dialog |

All menu wiring lives in `MainWindow.axaml` (the `<Menu>` at the top) and
`MainWindow.axaml.cs`. Every menu item maps to the exact same action the
original ToolStripMenuItem did - grep `MainWindow.axaml.cs` for the "Replaces
..." comments to trace each one back to its WinForms source.

## 2. What's fully working

- Main hunt tracker: start/stop/reset, target selection (via the ported
  Pokémon selector), live sprites, session encounter table, lifetime stats.
- Compact overlay window (draggable, pinnable, reuses the main ViewModel).
- Appearance/theme settings (presets, custom image, color pickers).
- Client selection when multiple PRO clients are running.
- Boss database browser + boss detail view (rewards, team, per-Pokémon detail).
- Boss cooldown tracker.
- Counterpart browsers (all 11 event groups) with inline detail panel.
- Lifetime hunting stats.
- Kanto route browser with encounter display.
- Mega Stones guide viewer.
- All the empty/placeholder screens from the original now show a "Coming
  Soon" window instead of silently doing nothing.

## 3. Deliberate simplifications (read before "fixing" these)

- **Floating popups → inline panels.** `PokemonFormsPopup`,
  `BossPokemonCard`, and `CounterpartHoverForm` were all separate floating
  Form windows positioned next to a clicked card, with manual
  screen-edge-avoidance math. Each is now an inline panel that
  appears/updates in the same window instead. Same information, no
  positioning logic to maintain. If you specifically want floating popups
  back, follow the same pattern as the rest of the app: new ViewModel +
  `Window` with `SystemDecorations="None"`, positioned via
  `PixelPoint`/`Screens` off the source control's screen coordinates.

- **Map pin overlays not ported.** The original's `KantoMapForm` had
  clickable marker controls positioned at exact pixel coordinates over a map
  image (set in the WinForms Designer). Those coordinates don't translate
  automatically. `RegionMapWindow` gives full access to every route via the
  left-hand list instead. To add pins back: overlay `Button`s on a
  `Canvas`/`Grid` on top of the map `Image`, using coordinates expressed as a
  fraction of the image size (same idea as `CaptureRegion` elsewhere in this
  codebase) so they scale with the window.

- **WebView2 → WebViewControl-Avalonia.** `GuideWindow.axaml` references
  `WebViewControl-Avalonia`'s `WebView` control (`xmlns:webview="clr-namespace:WebViewControl;assembly=WebViewControl.Avalonia"`).
  This is a CEF-based control and a heavier dependency than WebView2 (which
  ships with Windows). **Verify the exact namespace/control name against
  whatever version restores** - package APIs shift between versions and this
  wasn't compile-checked. If you'd rather avoid a CEF dependency entirely,
  the guide content is just local static HTML/CSS - consider rendering it
  with a Markdown-to-Avalonia converter or plain `TextBlock`/`Image` layout
  instead of embedding a browser at all.

- **PVP Stats.** `PVPStats.cs` in the original never actually wired its
  labels to a data source (no `PVPStatsService` exists anywhere in the
  codebase) - it's category titles with no values. `PvpStatsWindow.axaml` is
  a faithful 1:1 port of that same unfinished state, not a bug.

- **Compact mode "Pause".** The original's compact-mode Pause button called
  `huntSession.Pause()` directly without saving the session. The Avalonia
  compact window reuses `MainWindowViewModel.StopCommand` instead, which
  also persists the session - a small, deliberate improvement (less risk of
  losing progress if the app closes unexpectedly).

## 4. Windows-only pieces (unavoidable, not a WinForms artifact)

Unchanged from before - these read/capture *another process's window* (the
PRO game client) via Win32 `user32.dll` interop and GDI `CopyFromScreen`,
which is inherently Windows-specific:

- `Services/NativeMethods.cs`, `Services/WindowCaptureService.cs`
- Everything in `Tracking/` (`ScreenCapture.cs`, `ProWindowFinder.cs`,
  `EncounterDetector.cs`, `CatchDetector.cs`, `RareEncounterDetector.cs`,
  `BattleWindowLocator.cs`, `EncounterTracking.cs`)

Marked `[SupportedOSPlatform("windows")]`. Everything else in the app (UI,
data browsing, appearance, stats) runs on Linux/macOS too; only live
encounter tracking against the PRO client needs Windows.

## 5. Things worth a second look when you build this

Since this was hand-written without a compiler, expect a handful of
first-pass issues - normal for a conversion this size:

- **`WebViewControl-Avalonia` namespace/API** (see §3) - most likely thing to
  need adjusting.
- **Compiled-binding edge cases** - a few `DataTemplate`s (e.g. the
  background-preset buttons in `AppearanceWindow.axaml`) don't declare
  `x:DataType`, so they fall back to classic/reflection bindings rather than
  compiled ones. That's intentional and should just work, but if Avalonia's
  compiler complains, adding an explicit `x:DataType` to that template is
  the fix.
- **`ColorPicker`** in `AppearanceWindow.axaml` is Avalonia's built-in
  control (ships in the base `Avalonia` package as of 11.x) - double check
  it resolves under the default `avaloniaui` namespace with the packages
  that actually restore.
- **`Avalonia.Controls.DataGrid`** needs its own package + the
  `Fluent.xaml` style include in `App.axaml` (already added) - if the boss
  cooldown grid or session-encounter grid don't render, check that package
  version matches the core `Avalonia` version.

## 6. Building

```
cd FootTracker.Avalonia
dotnet restore
dotnet build
```

## 7. Cross-platform status (Linux / macOS)

The app itself (UI, Appearance, Boss database, Counterparts, Boss cooldowns,
Hunting stats, guides, import/export) runs on any OS Avalonia supports. Live
window capture and encounter/catch OCR detection - the part that watches the
PRO game client - is genuinely platform-specific and is being built out in
phases:

### Phase 1 - window finding/capture (done)

`Tracking/Capture/IWindowCaptureService.cs` abstracts "find PRO client
windows" and "capture one as PNG bytes" behind an interface, picked at
runtime by `WindowCaptureServiceFactory`:

- **Windows** (`WindowsWindowCaptureService.cs`) - wraps the original,
  tested `ScreenCapture.cs`/`ProWindowFinder.cs` (Win32 `user32.dll` +
  `PrintWindow`), unchanged.
- **Linux** (`LinuxX11WindowCaptureService.cs`) - shells out to `wmctrl`
  (window listing, cross-checked against `/proc/<pid>/comm`) and
  `import`/`maim` (capture) rather than P/Invoking libX11 directly, since
  that's far easier for a tester without a dev environment to debug (they
  can run the same commands themselves). **X11/XWayland only** - native
  Wayland windows aren't visible this way; that needs the xdg-desktop-portal
  ScreenCast API (D-Bus + PipeWire), which is a separate, larger piece of
  work if you need it.
- **macOS** (`MacOSWindowCaptureService.cs`) - uses tools that ship with
  every stock macOS install (no extra packages needed, unlike Linux):
  `ps` to find PROClient's PID, `osascript` (AppleScript via System Events)
  to read that process's front window position/size/title, and
  `screencapture -R x,y,w,h` to grab that screen region to a PNG file.
  **Two one-time permission grants are required** (System Settings >
  Privacy & Security): "Automation" access for this app to control System
  Events, and "Screen Recording" access for `screencapture` to return real
  pixel data. Missing either one usually shows up as a blank/black capture
  rather than a clean error - see the troubleshooting note below.

  **Known limitation:** this captures a *screen region*, not the window's
  own compositor buffer (unlike Windows' `PrintWindow` or the Linux
  backend's `import -window`). If another window overlaps the PRO client
  while scanning, the capture will show whatever's on top instead. This was
  a deliberate choice to avoid hand-written Core Foundation P/Invoke
  marshaling (`CGWindowListCopyWindowInfo`/`CGWindowListCreateImage`) that
  couldn't be verified without a Mac to test against. If occlusion turns
  out to be a real problem in practice, that's the upgrade path - happy to
  build it once there's a concrete failure report to work from rather than
  guessing blind.

### Phase 2 - OCR detection pipeline (done)

`System.Drawing.Common` does not work at all outside Windows in modern .NET
(throws `PlatformNotSupportedException` for basically any `Bitmap`/`Graphics`
operation, not just screen capture) - so the entire detection pipeline
needed migrating, not just the capture step:

- **`ImageOps.cs`** (new) - SkiaSharp-based crop/resize/threshold helpers,
  replacing the `System.Drawing.Bitmap`/`Graphics` calls the pipeline used
  to make directly.
- **`BattleWindowLocator.cs`, `EncounterDetector.cs`, `CatchDetector.cs`,
  `RareEncounterDetector.cs`** - all rewritten to use `SkiaSharp.SKBitmap`/
  `SKRectI` instead of `System.Drawing.Bitmap`/`Rectangle`. The actual
  detection logic (thresholds, region percentages, OCR text matching) is
  untouched - only the imaging API calls changed.
- **Tesseract feeding** - switched from `Tesseract.Drawing`'s
  `PixConverter.ToPix(bitmap)` (needs `System.Drawing.Bitmap`, Windows-only)
  to `Pix.LoadFromMemory(pngBytes)` (part of the core `Tesseract` package,
  works identically on every OS). This let the `Tesseract.Drawing` package
  reference be removed entirely, and means Windows and Linux/macOS now run
  through the *exact same* OCR code path - no per-platform duplication.
- **`ScreenCapture.cs`** - trimmed down to just the Windows-only
  `PrintWindow` capture (used internally by `WindowsWindowCaptureService`).
  The pure image-math methods that used to live here (`CropImage`,
  `GetBattleTitleRegion`, `DrawDebugRegion`) moved to `ImageOps.cs` /
  `BattleWindowLocator.cs`.
- **`EncounterTracking.cs`** (the polling-loop orchestrator) - now captures
  via `IWindowCaptureService.CaptureSelectedWindowPng()` + `SKBitmap.Decode`
  instead of calling `ScreenCapture.CaptureProWindow()` directly, so the
  same polling loop runs on Windows and Linux without an `if (IsWindows)`
  branch anywhere in the detection logic itself.

**Net effect:** once a Linux tester has `wmctrl` + `import`/`maim`
installed and is on X11 (or XWayland), live encounter/catch/shiny detection
should work exactly as it does on Windows - this was genuinely the larger
of the two phases, since it touched every file in the detection pipeline,
not just the capture boundary.

**Net effect:** once a Linux tester has `wmctrl` + `import`/`maim`
installed and is on X11 (or XWayland), and a macOS tester has granted the
Automation + Screen Recording permissions, live encounter/catch/shiny
detection should work the same as on Windows - Phase 2 was genuinely the
larger of the two, since it touched every file in the detection pipeline,
not just the capture boundary.

**What to ask a tester for if detection doesn't work:**

- **Linux:** the `StatusMessage` shown in the toolbar (it'll say things
  like "Waiting for PROClient..." or surface `LastError` from the capture
  service), and whether they're on X11 or Wayland (`echo $XDG_SESSION_TYPE`).
- **macOS:** same `StatusMessage`/`LastError`, plus whether they've granted
  both permissions (System Settings > Privacy & Security > Automation, and
  > Screen Recording - the app needs to appear and be checked in both). A
  captured image that's entirely black/blank almost always means Screen
  Recording wasn't granted, not a code bug.
- **Both:** ideally a screenshot of the PRO client window at the moment
  detection should have fired - since the OCR thresholds/regions were
  tuned against specific screenshots and may need adjusting for a different
  theme/resolution/GUI scale, independent of the platform work.


## 8. Sprite asset packaging (Assets.pak)

`SharedPokemonLibrary/Assets` is ~3,200 loose PNG files (~29MB). For
distribution, these ship as one compressed `SharedPokemonLibrary/Assets.pak`
(a plain ZIP archive - there's no universal ".pak" format, it's just a naming
convention) instead of thousands of loose files - roughly a 30% size
reduction, plus it avoids the filesystem/packaging overhead of that many tiny
files when you zip up a build to send to someone.

- **`Services/AssetPakService.cs`** unpacks `Assets.pak` into
  `SharedPokemonLibrary/Assets/` once, the first time the app runs after a
  fresh install (skipped entirely if the loose folder already has files in
  it - e.g. a dev checkout that still has the original loose assets). Called
  first thing in `App.axaml.cs`, before anything tries to load a sprite.
- **Nothing else changed.** Every sprite/image-loading call site
  (`PokemonSpriteService`, `CounterpartSpriteService`, `ThemeManager`, etc.)
  still reads loose files by path exactly as before - they have no idea a
  `.pak` was ever involved.
- **The `.csproj`** now ships `Assets.pak` if it exists, and only falls back
  to the old "include every loose file" glob if it doesn't - so nothing
  breaks if you're working from a checkout without a pak built yet.

**Rebuilding `Assets.pak` after changing sprites:** it's just a ZIP archive
with the same relative folder structure as `SharedPokemonLibrary/Assets/`
(e.g. entry `Sprites/25.png`, not `Assets/Sprites/25.png`). Any zip tool
works, e.g. from PowerShell in the project root:

```powershell
Remove-Item "SharedPokemonLibrary\Assets.pak" -ErrorAction SilentlyContinue
Compress-Archive -Path "SharedPokemonLibrary\Assets\*" -DestinationPath "SharedPokemonLibrary\Assets.pak" -CompressionLevel Optimal
```

**Optional follow-up:** the loose `SharedPokemonLibrary/Assets/` folder is
only needed as the *source* for rebuilding the pak - once you're confident
`Assets.pak` works, you could stop tracking the loose files in source
control and keep only the pak, shrinking the repo itself too. Not required;
the `.csproj` condition means both can coexist safely either way.

## 9. Local build size (RuntimeIdentifier)

If your `bin\Debug`/`bin\Release` folders are gigabytes in size, this is why:
without a `RuntimeIdentifier` pinned in the `.csproj`, a normal build can't
tell which platform it's targeting, so it copies **every** platform's native
binaries (SkiaSharp, HarfBuzz, Tesseract's leptonica/tesseract libs) from
every cross-platform package into the output at once - Windows, Linux, and
macOS versions all together, even though you can only run one of them. This
is a well-known .NET/Avalonia gotcha, not something specific to this project.

The `.csproj` now pins `<RuntimeIdentifier>win-x64</RuntimeIdentifier>`, so a
plain build/F5 in Visual Studio only includes Windows' native binaries.

**Important:** this is written as `Condition="'$(RuntimeIdentifier)' == ''"`
- a *default*, not a hard override. An earlier version of this fix pinned it
unconditionally, which silently broke cross-platform publishing entirely:
`dotnet publish -r osx-arm64` would still build for win-x64 under the hood,
because the unconditional csproj assignment evaluates after (and overwrites)
the command-line `-r` value. If a published build for another platform ends
up with the wrong platform's native assemblies in it (e.g. `Avalonia.Win32.dll`
showing up in an `osx-arm64` publish folder), this conditional is the thing
to check first.

**After pulling this change, delete the old bloated output once** (old files
don't shrink on their own - a stale build stays stale until you clean it):

```
Remove-Item -Recurse -Force bin, obj
```

Then rebuild. `bin\Debug\net10.0\` should now be a small fraction of its
previous size - back in the same ballpark as the original WinForms output,
plus whatever the actual new dependencies (Avalonia, SkiaSharp, Tesseract)
genuinely need for one platform.

**Publishing for your Linux/macOS testers** now needs an explicit RID
override, since the pinned default is `win-x64`:

```
dotnet publish -r linux-x64 -c Release
dotnet publish -r osx-x64 -c Release      # Intel Macs
dotnet publish -r osx-arm64 -c Release    # Apple Silicon Macs
```

`SelfContained` is now a project default (bundles the .NET runtime itself
into the output, so testers don't need .NET installed separately - this
adds real, necessary size, unlike the accidental multi-platform duplication
above, typically tens of MB). Add `-p:SelfContained=false` to override it
back to framework-dependent for a
smaller output if your testers already have the .NET 10 runtime installed.

## 10. Single-file publish - tried, reverted (confirmed real bug)

**This was reverted.** Originally set up to bundle every DLL into one exe
purely for a cleaner-looking output folder. Confirmed, via an actual crash
report and log, that it's incompatible with how `TesseractOCR` loads its
native libraries:

`TesseractOCR.InteropDotNet.LibraryLoader` is a custom native-library loader
(not plain .NET `DllImport` resolution) - it manually searches for loose
`leptonica`/`tesseract` DLLs in folders next to the exe. Single-file
publishing (`IncludeNativeLibrariesForSelfExtract=true`) swept those DLLs
into the bundled exe, making them invisible to that custom loader. Result:
`System.DllNotFoundException: Failed to find library 'leptonica-1.85.0.dll'
for platform x64` the moment OCR tried to initialize - i.e. tracking would
launch fine and immediately fail on the first encounter, every time.

The cosmetic win (fewer visible DLLs) wasn't worth a real OCR-breaking bug,
so `PublishSingleFile` and `IncludeNativeLibrariesForSelfExtract` were
removed from the `.csproj`. `SelfContained` stays on - that part was never
the problem, and it's still genuinely useful (testers don't need .NET
installed separately).

**If you want to revisit single-file publishing later**, the real fix would
be finding a way to keep specifically the native OCR DLLs loose (so
`TesseractOCR`'s own loader can still find them) while still bundling
everything else - not attempted here, since it would need to be verified
against an actual build rather than guessed at again.

**Practical result:** publish output folders have loose DLLs visible again,
same as before this was tried. `ExcludeFromSingleFile` metadata was left on
the `Content` items (tessdata, Data, DataFiles, SharedPokemonLibrary) - it's
a no-op with single-file publishing off, but harmless to leave in place.

```
dotnet publish -r win-x64 -c Release
```

The result lands in `bin\Release\net10.0\win-x64\publish\` - that folder is
what you'd hand to a tester, not `bin\Debug`. It'll contain a `.pdb` file
(debug symbols) alongside the exe; safe to delete before sharing, or add
`<DebugType>none</DebugType>` to the `.csproj` to stop it being generated on
publish at all.

## 11. OCR package: Tesseract -> TesseractOCR (Linux/macOS fix)

The original `Tesseract` NuGet package (charlesw) only ships **Windows**
native binaries - confirmed via their own open GitHub issue (#503, "Linux
support missing for .NET Core"). On Linux, `TesseractEngine` initialization
would throw `System.DllNotFoundException: Failed to find library
"libleptonica-X.so"` the moment OCR tried to run - nothing to do with any of
the RID/publish work elsewhere in this guide, just a package that never had
Linux/macOS binaries in the first place. This wasn't caught during the
original Phase 2 OCR pipeline rewrite, since that work made the *code* (the
detector files) cross-platform via SkiaSharp, but never verified the
underlying *native Tesseract binaries themselves* existed for Linux.

**Switched to `TesseractOCR`** (Sicos1977's fork on GitHub/NuGet, Apache 2.0
- free, not commercial): an actively-maintained fork of the same original
project, explicitly bundling native Tesseract 5.x binaries for Windows x64,
Linux x64, and macOS in the NuGet package itself. (IronOCR was also
considered - it's genuinely cross-platform too, but it's a commercial/paid
product, not appropriate to introduce into a free tool without a clear,
separate decision to pay for a license.)

**API differences**, already migrated in `EncounterDetector.cs`,
`CatchDetector.cs`, and `RareEncounterDetector.cs`:

| Old (`Tesseract`) | New (`TesseractOCR`) |
|---|---|
| `using Tesseract;` | `using TesseractOCR;` + `using TesseractOCR.Enums;` |
| `TesseractEngine` | `Engine` |
| `new TesseractEngine(path, "eng", EngineMode.Default)` | `new Engine(path, Language.English, EngineMode.Default)` |
| `engine.DefaultPageSegMode = PageSegMode.X` | Removed - `PageSegMode` is now passed directly to `Process(...)` instead |
| `Pix.LoadFromMemory(bytes)` | `TesseractOCR.Pix.Image.LoadFromMemory(bytes)` |
| `engine.Process(pix, PageSegMode.X)` | `engine.Process(image, PageSegMode.X)` (same shape, different types) |
| `page.GetText()` | `page.Text` (property, not a method) |

The `tessdata/eng.traineddata` file didn't need to change - both packages
consume the same standard Tesseract trained-data format; only the .NET
wrapper and its bundled native binaries changed.

## 12. tessdata missing from a Windows single-file publish (superseded)

Observed: after a confirmed clean `bin`/`obj` rebuild, a `win-x64` single-file
publish was missing the `tessdata` folder entirely (Linux/macOS publishes
from the same source had it). `ExcludeFromSingleFile` was added to every
`Content` item as the fix at the time.

**Update:** single-file publishing was subsequently reverted entirely (see
§10) after it turned out to also break `TesseractOCR`'s native library
loading in a related but separate way. With single-file publishing off,
this specific `tessdata` symptom is moot - `ExcludeFromSingleFile` is a
no-op without `PublishSingleFile`, but was left on the `Content` items
harmlessly. Keeping this section for the record in case single-file
publishing is ever revisited.