# Linux tracking investigation - 2026-08-31 (evening report, 18:13:01 bundle)

Follow-up to `LINUX-REGRESSION-REPORT-2026-08-31.md` (this morning). That report ended with the root
cause unconfirmed and asked for a Report a Problem bundle from the affected machine. The bundle
arrived, and it settles the question. This is the full chain, the fix that shipped as MIGRATION_GUIDE
section 142, and the message for the tester.

## 1. The one-line answer

The battle-window locator finds a battle window on almost every battle frame on this machine - but the
box it finds starts about 189 pixels too far right, because the rule that rejects candidates touching
the frame's edges (added to filter out a bordered window's own OS title bar) throws away every CORRECT
candidate on a window this snug, leaving only too-narrow ones. Every OCR region is derived from that
box, so the title crop reads 'ld Nidoran M' - no "VS", no "Wild" - and no encounter can ever register.
The manual box the tester drew in Set Screen Boundaries was correct the whole time and was never
consulted, because it was only a fallback for when the scan finds NOTHING, and the scan always "found"
something.

## 2. What the bundle shows, line by line

Environment (log line 2): Bazzite Linux, Wayland session, capture via wmctrl+import through XWayland,
app version 1.0.0.0, running from `.../ProTrackerDatabase-LinuxX64/linux-x64/`.

The tracking check at 18:13:01 - every stage from section 134, doing exactly what it was built for:

    Capture tools: available
    PROClient windows: 1 found: PID 12874
    Bound window: yes
    Manual boundaries: set: 1220x718 at (0,402), drawn on a 1432x1222 frame; used only when automatic detection finds nothing
    Capture: 1432x1222, mean brightness 65/255
    Battle window: located at (189,396) 1053x619
    Title OCR: no 'VS' title could be read from the located window
    Tracking loop: state=Tracking; scans=745; last scan 1s ago; last good capture 0s ago; last failed capture never
    Last accepted encounter: none this session
    Recent errors: none

So: capture works, the loop is healthy, OCR runs, a box is located on nearly every battle frame
("battle window located in 79 of the 79 frames" in the heartbeats), and still zero encounters all day.
The only failing stage is the title read from the located box.

The detector logs say why. With the box at (189,396) 1053x619, the title crop (left + 30% of width,
52% wide) lands at x=504 - mid-"Wild" - and reads:

    BossBattleDetector OCR attempt: text='ld Nidoran M', titleRegion=(504,419,548x56), battleBounds=(188,419,1054x620)

'ld Nidoran M' contains neither "vs" nor "wild", so the section-111 gate correctly refuses it, every
frame, forever. The catch detector reads the Poke Ball row as 'DD' from a message region derived from
the same wrong box. The annotated report frame shows all five detector regions shifted right in colour.

The saved manual box changed a few times during the session as the tester re-drew it - (20,418)
1204x708, then (16,396) 1224x720, then (0,402) 1220x718 - all of them essentially right (the real
battle window on this frame is at (0..14, 396) about 1230 wide). The log also shows the fallback being
consulted only between battles: "Manual screen boundaries: the saved box does not currently hold a
battle window" - correct behaviour out of battle, and the only time the fallback ever ran, because in
battle the scan always succeeded first.

## 3. The mechanism, replicated on the report frame itself

A literal Python port of the shipped locator was run on the bundle's own annotated frame (the overlay
boxes inpainted out first). It reproduces the tracker's box exactly: (189,396) 1053x619.

Two things combine:

1. **The edge rule discards every correct candidate.** The title bar's dark run starts ~6 px from the
   frame's left edge (PRO at GUI scale ~1.57 nearly fills the 1432-wide window; battle window ~1230 of
   1432 = 86% of the width, ~7% clearance per side). The rule `battleX < width * 2%` - built from six
   Windows screenshots whose battle windows all had 12-26% clearance, to reject the OS title bar of a
   bordered window - rejects every candidate built from a correct run.
2. **The survivors under-reach.** With correct candidates gone, the widest surviving run starts past
   the logo's star rays (~x=279), and the fixed 9.5% left expansion - measured at GUI scale 1, where
   the logo really is ~9.5% of the run - cannot bridge 189 px. Box lands at (189,396) 1053 wide.

Why it "worked after 8/24 and stopped after 8/27": the confirmed-working 8/26 build (GitHub branch
`Beta-3.0-...`) has the same colour scan but NO edge rule and first-accepted-run selection. The edge
rule and the widest-run rework shipped in the 8/27-8/30 window. On this window shape the edge rule is
the difference between a working locate and a wrong one. (Side finding: that branch's name ends
without a closing parenthesis - `Beta-3.0-(Cross-Platformed-again)-(Linux-and-Windows-confirmed-working,-no-word-on-MacOS`
- so the GitHub download link, which appends one, resolves to the wrong branch; that is why downloads
"provided files not associated with that Github". The two working copies in Downloads are that 8/26
linux-x64 publish and the Beta-2.0 source tree.)

## 4. The fix (MIGRATION_GUIDE section 142)

1. **Edge rule scoped to the top rows.** An edge-touching candidate is rejected only when its top is
   in the first 5% of the frame - where a bordered window's OS title bar actually lives. On the report
   frame the same scan then finds (0,391) 1243x731, and the title crop holds the full
   'VS. Wild Nidoran M'. Re-run over the eighteen stored real frames from earlier investigations
   (three window sizes, two GUI scales, in and out of battle): the located box is identical with and
   without the scope on every single one.
2. **The saved manual box now overrides a disagreeing scan.** After a successful scan, if a saved box
   placed on this frame disagrees with the scan's box (left/top off by >3% of the frame, or width by
   >8%) and itself holds a battle window right now (the same presence test the fallback uses), the
   player's box wins, and the status line / tracking check say so. Agreeing boxes keep the scan's
   result; no saved box means no change; out of battle the presence test fails and behaviour is
   exactly as before. On the report frame this change ALONE also fixes tracking.

Either change independently rescues this machine; together the scan is right again and the player's
box is the safety net the Set Screen Boundaries feature was meant to be.

Also fixed by consequence: the counterpart matcher's GUI-scale inference (battle width / 782) now
receives the true width on these frames.

## 5. What to verify on the tester's machine

1. Update to the new build. Start a hunt at the same window size (1432x1222).
2. In battle, the status line should show the encounter line ("Encountered Lv. 27 Nidoran M ...").
   If the saved box is in use it says so in the log: "the automatic box ... disagrees with the saved
   box ... using the saved box".
3. Run Report a Problem once: the tracking check's "Battle window" line should read a box starting at
   x <= 20 and ~1220-1245 wide (either from the scan or "(through the saved manual boundaries)"), and
   "Title OCR" should name the Pokemon.
4. If everything reads correctly, the saved boundaries can stay (they are inert while the scan
   agrees) or be cleared in Set Screen Boundaries - either is fine.

## 6. Message for the tester (Discord-ready)

> Found it, and it was on our side - your setup was fine and your saved boundaries were right.
> Your report bundle showed the tracker was finding the battle window every frame, but the box it
> found started ~190 pixels too far right at your window size, so the text reader was looking at the
> wrong strip of the title bar and never saw "VS. Wild ...". That's why it looked healthy but never
> counted anything, and why it used to work before the 8/27+ updates - the rule that broke it shipped
> in that window. Two fixes are in the next build: the detection itself is corrected for large GUI
> scales in snug windows, and the box you drew in Set Screen Boundaries now takes over whenever the
> automatic detection disagrees with it during a battle. Your saved box was spot on, so either way the
> next build should track for you immediately. One Report a Problem after updating would confirm it -
> thanks for the bundle, it's what cracked this.

## 7. Honest limits

The colour scan itself is unchanged; a theme or scale nobody has measured can still mislead it - the
difference is a drawn box now beats a wrong scan during battles, and the status line says which box is
in use. Out-of-battle frames may report "a battle-window shape is on screen" slightly more often (the
mid-frame edge-touching candidates the old rule suppressed), which changes no counters - the "vs"/
"wild" gate and the name catalogs still stand between a shape and an encounter. The identified defect
is fixed and passes the listed tests. Confirmation on the affected user's machine is still pending.
