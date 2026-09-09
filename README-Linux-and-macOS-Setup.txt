Pro Tracker - Linux and macOS setup
====================================

This app runs on Windows, Linux, and macOS, but window capture and the OCR
engine it uses to detect encounters need a couple of one-time, OS-specific
setup steps that a normal Windows install doesn't. See MIGRATION_GUIDE.md
(sections 7, 17, 18, 21, and 24) for the full technical history of why.


LINUX
-----

Easiest path (recommended - use this if you were handed a zip that already
has a "linux-start.sh" file sitting next to a "linux-x64" folder): from a
terminal, in the folder you extracted, run

    bash linux-start.sh

(Use "bash linux-start.sh" rather than "./linux-start.sh" the first time -
a zip made on Windows and unzipped on Linux can lose the file's execute
permission, and running it through bash directly sidesteps that entirely.
This script fixes its own permission for next time, but the very first run
still needs "bash" in front of it.)

This one command runs the setup script described below against the
linux-x64 folder (system packages, the .NET SDK if needed, and the OCR
native-library fix), quiets the Linux/GNOME "bell" beep sound if it can,
and then starts the app. Nothing else to do. Safe to run more than once,
same as linux-setup.sh itself - every time you get a new build, this is
still the only command you need.

(Packaging note: linux-start.sh lives at the project root, next to this
README - it does NOT get copied into the linux-x64 publish folder
automatically the way linux-setup.sh and linux-fix-tesseract-ocr.sh do,
since its whole job is to sit one level above that folder. When putting
together a zip for someone else, include linux-start.sh and this README
alongside the linux-x64 folder, all three as siblings.)

If you don't have a linux-start.sh (for example, you're working from a
source checkout instead of a handed-off zip, or someone gave you just the
contents of the linux-x64 folder on its own), run the setup script directly
instead - it does everything except the bell-quieting and launch steps
above. From a terminal, in the folder you extracted (whether that's the
full source checkout or a published build someone handed you as a zip):

    bash linux-setup.sh

(Use "bash linux-setup.sh" rather than "./linux-setup.sh" the first time -
same reason as above.)

This one command:
  - installs the system packages this app needs (wmctrl, imagemagick, and
    the Tesseract/Leptonica OCR libraries) using whatever package manager
    your distro uses - apt, dnf, yum, pacman, zypper, or apk;
  - installs the .NET 10 SDK for you if it isn't already on your system
    (to your own home folder, no root needed for that part);
  - builds the app for Linux if you gave it a source checkout, or uses an
    already-published build if you gave it one instead;
  - fixes a native-library-loading bug in the OCR package itself (see
    MIGRATION_GUIDE.md §17/§18 - this is a real bug in that package, not
    something specific to this app, but this app needs the workaround
    regardless);
  - fixes the executable's permissions if they got lost along the way.

It's safe to run more than once. Run it again any time you get a new build
and want to double-check everything is still in place.

If it can't do something automatically (for example, no internet access to
your distro's package repositories, or you're on an "atomic"/immutable
Fedora variant like Silverblue, Kinoite, or Bazzite, where packages can't be
installed live), it will tell you exactly what happened and what to run by
hand instead - it won't just fail silently.

Manual fallback, if you'd rather not run the script (or it can't finish for
some reason) - install these yourself, then run linux-fix-tesseract-ocr.sh
from inside the app's publish folder:

    Debian/Ubuntu:  sudo apt install wmctrl imagemagick libtesseract-dev libleptonica-dev
    Fedora/RHEL:    sudo dnf install wmctrl ImageMagick tesseract-devel leptonica-devel
    Arch:           sudo pacman -S wmctrl imagemagick tesseract leptonica
    openSUSE:       sudo zypper install wmctrl ImageMagick tesseract-ocr-devel leptonica-devel

    Fedora Atomic / immutable (Bazzite, Silverblue, Kinoite, etc.) - regular
    dnf is blocked on these; use rpm-ostree and reboot afterward:
        rpm-ostree install wmctrl ImageMagick tesseract-devel leptonica-devel
        (then reboot before trying to run the app)

One more thing worth knowing: window capture on Linux only sees X11 windows
(this includes XWayland, which is how most Wayland desktops still show X11
apps, so this is usually a non-issue). A window running under native
Wayland with no XWayland compatibility layer won't be visible to it. Run
"echo $XDG_SESSION_TYPE" in a terminal if you want to check which you're on.


MACOS
-----

GETTING IT TO START (do this first): a zip made on Windows cannot carry the
"this file is a program" permission macOS requires, and macOS additionally
quarantines everything downloaded from the internet - so a freshly
extracted build will NOT open by double-clicking. It looks like the app
simply doesn't load; nothing appears at all. From Terminal, in the folder
you extracted, run:

    bash macos-start.sh

(Use "bash macos-start.sh" rather than "./macos-start.sh" the first time -
the script's own execute permission is lost to the same Windows-zip
limitation. It fixes its own permission for next time. Safe to run more
than once - every time you get a new build, this is still the only command
you need.)

That one command restores the executable's permissions, clears the
quarantine flag so Gatekeeper allows the launch, sets up the OCR libraries
via Homebrew if it's installed (encounter detection needs them; the app
runs without them), and starts the app.

Manual fallback, if you'd rather do it by hand - from Terminal, inside the
folder that contains "ProTrackerDatabase.Avalonia":

    chmod +x ProTrackerDatabase.Avalonia
    xattr -dr com.apple.quarantine .
    ./ProTrackerDatabase.Avalonia

If macOS still refuses with an "unidentified developer" / "Apple could not
verify" message, approve it under System Settings > Privacy & Security >
"Open Anyway", then run it again. (This app isn't notarized with Apple -
that's what macOS is objecting to, not anything actually wrong with it.)

Intel vs Apple Silicon: use the build matching the Mac - osx-x64 for Intel
Macs, osx-arm64 for Apple Silicon (M1/M2/M3/M4). An arm64 build cannot run
on an Intel Mac at all. The Intel build does run on Apple Silicon through
Rosetta, if Rosetta is installed ("softwareupdate --install-rosetta").

If the app STILL doesn't start: it may be crashing while starting up
rather than being blocked from launching. In that case it writes a crash
log - send the newest file from
~/Library/Application Support/ProTracker/Logs/ to the developer, plus
whatever the Terminal printed. If no such folder/file exists, the app was
never allowed to run at all - re-check the steps above.

ONCE IT RUNS: no extra packages are needed for window capture - macOS
ships everything this app's capture backend needs out of the box. Instead,
two one-time PERMISSION grants are needed (System Settings > Privacy &
Security):

  - "Automation" - lets the app read the PRO client window's position/size
    by asking System Events for it.
  - "Screen Recording" - lets the app actually capture pixels from that
    window. Without this, captures come back black/blank rather than
    showing a clear error, so if tracking silently isn't picking anything
    up, this is the first thing to check.

The app needs to appear (and be checked) in both lists, not just one.

Known limitation: capture on macOS grabs a region of the screen, not the
window's own buffer directly - if another window overlaps the PRO client
while it's scanning, the capture will show whatever's on top instead. Move
the PRO client window to the front/unobstructed if detection seems to be
missing things intermittently.


REPORTING A PROBLEM ON EITHER OS
---------------------------------

The in-app "Report a Problem" feature captures a screenshot of the PRO
client with colored boxes drawn over exactly what each detector is reading
from, plus the log file, which is usually far more useful for tracking down
what went wrong than a description alone. The log file itself lives at:

    Linux:   ~/.local/share/ProTracker/Logs/
    macOS:   ~/Library/Application Support/ProTracker/Logs/

The same log also records startup crashes: if the app opens and then
immediately closes (or never shows a window despite being allowed to run),
the newest protracker-*.log file in that folder says exactly which startup
step failed and why - that plus the terminal output is everything the
developer needs. The app also shows a "Pro Tracker - startup error" window
for startup failures whenever it gets far enough to be able to.
