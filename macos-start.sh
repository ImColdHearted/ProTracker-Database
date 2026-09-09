#!/bin/bash
# Pro Tracker - one-command macOS setup + launcher.
#
# WHY THIS EXISTS: the macOS builds of this app are published and zipped on a
# Windows machine, and a zip made on Windows cannot carry Unix file
# permissions - so the app arrives WITHOUT the "executable" bit that macOS
# needs before it will run anything. On top of that, macOS marks everything
# downloaded from the internet with a "quarantine" flag, and Gatekeeper
# refuses to launch quarantined programs that aren't notarized by Apple
# (double-clicking does NOTHING visible in the Finder for a bare executable
# without its execute bit - which is exactly what "the app does not load"
# looks like). This script fixes both, sets up OCR's native libraries if
# Homebrew has them, and then starts the app.
#
# USAGE (from Terminal, in the folder you extracted the zip into):
#
#     bash macos-start.sh
#
# Use "bash macos-start.sh" rather than "./macos-start.sh" the first time -
# this script's own execute permission is lost to the same Windows-zip
# limitation described above. It fixes its own permission for next time.
# Safe to run more than once - every time you get a new build, this is
# still the only command you need.
#
# (Same idea as linux-start.sh for Linux testers - see
# README-Linux-and-macOS-Setup.txt and MIGRATION_GUIDE.md §86.)

set -u

APP_NAME="ProTrackerDatabase.Avalonia"
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

echo "Pro Tracker - macOS setup + launch"
echo "==================================="
echo ""

# --------------------------------------------------------------------------
# 1. Find the publish folder: either this script sits INSIDE it (next to the
#    executable), or it sits one level above a folder named osx-arm64/osx-x64
#    (the way linux-start.sh sits next to a linux-x64 folder).
# --------------------------------------------------------------------------
APP_DIR=""
if [ -f "$SCRIPT_DIR/$APP_NAME" ]; then
    APP_DIR="$SCRIPT_DIR"
else
    for candidate in osx-arm64 osx-x64; do
        if [ -f "$SCRIPT_DIR/$candidate/$APP_NAME" ]; then
            APP_DIR="$SCRIPT_DIR/$candidate"
            break
        fi
    done
fi

if [ -z "$APP_DIR" ]; then
    echo "Could not find the app executable ($APP_NAME)."
    echo "Run this script either from inside the app's folder, or from a"
    echo "folder that contains an 'osx-arm64' or 'osx-x64' folder."
    exit 1
fi

echo "App folder: $APP_DIR"

# --------------------------------------------------------------------------
# 2. Architecture sanity check.
#
#    An osx-arm64 build cannot run on an Intel Mac at all. The reverse - the
#    osx-x64 build on Apple Silicon - technically runs under Rosetta, and
#    §122 stopped presenting that as a supported route.
#
#    It is not one. That combination needs TWO things this Mac probably does
#    not have: Rosetta itself, and a second, Intel Homebrew under /usr/local
#    to supply x86_64 OCR libraries, because the Apple Silicon Homebrew at
#    /opt/homebrew only has arm64 ones (see §121). Two installs to reach
#    what the osx-arm64 build gives for nothing.
#
#    And Rosetta is on a clock: macOS 26.4 warns when launching Intel-only
#    apps, macOS 27 removes Rosetta on upgrade and reinstalls it only on
#    demand, and macOS 28 stops running Intel apps altogether. Advice built
#    on it expires.
#
#    So someone here has almost certainly downloaded the wrong zip, and the
#    useful thing to say is which one to get - not how to prop this one up.
# --------------------------------------------------------------------------
MACHINE_ARCH="$(uname -m 2>/dev/null || echo unknown)"
case "$APP_DIR" in
    *osx-arm64*)
        if [ "$MACHINE_ARCH" = "x86_64" ]; then
            echo ""
            echo "WARNING: this build is for Apple Silicon (arm64), but this Mac"
            echo "reports an Intel (x86_64) processor - it will not run here."
            echo "Ask for the osx-x64 build instead."
        fi
        ;;
    *osx-x64*)
        if [ "$MACHINE_ARCH" = "arm64" ]; then
            echo ""
            echo "  ################################################################"
            echo "  #  THIS IS THE INTEL BUILD, AND THIS IS AN APPLE SILICON MAC   #"
            echo "  ################################################################"
            echo ""
            echo "  Download the osx-arm64 build instead. It is the native one for"
            echo "  this Mac, it is faster, and it needs neither of the two extra"
            echo "  pieces this one does."
            echo ""

            # §122: check Rosetta rather than assume it. Without it the
            # exec at the end of this script dies with "Bad CPU type in
            # executable", which reads exactly like the app being broken -
            # the wrong-cause-in-the-message trap §115 was written for.
            #
            # This looks for Rosetta's runtime on disk rather than running
            # something through it. The obvious test, `arch -x86_64
            # /usr/bin/true`, can make macOS put up its "install Rosetta?"
            # dialog, and that would block this script mid-run waiting on a
            # window the reader may not even have in front of them. A setup
            # script should not be able to hang. Two markers are checked
            # because the path has moved between releases; a false negative
            # here only softens a secondary sentence, since both branches
            # lead with the same advice.
            ROSETTA_OK=0
            if [ -f /usr/libexec/rosetta/oahd ] || [ -d /Library/Apple/usr/libexec/oah ]; then
                ROSETTA_OK=1
            fi

            if [ "$ROSETTA_OK" -eq 0 ]; then
                echo "  As it stands this build most likely cannot start at all:"
                echo "  Rosetta does not appear to be installed, and without it"
                echo "  macOS refuses the executable outright with"
                echo "  \"Bad CPU type in executable\"."
            else
                echo "  Rosetta is present, so this build will start - but OCR will"
                echo "  still need an Intel Homebrew under /usr/local for its x86_64"
                echo "  libraries, which is a separate install from the one you have."
            fi

            echo ""
            echo "  If you genuinely cannot get the osx-arm64 zip, Rosetta installs"
            echo "  with:"
            echo "      softwareupdate --install-rosetta"
            echo "  Be aware Apple is retiring it - macOS 27 removes it on upgrade"
            echo "  and macOS 28 stops running Intel apps - so this is a stopgap,"
            echo "  not a fix."
            echo ""
        fi
        ;;
esac

# --------------------------------------------------------------------------
# 3. Restore the execute permission the Windows-made zip lost.
# --------------------------------------------------------------------------
chmod +x "$APP_DIR/$APP_NAME" 2>/dev/null || true
chmod +x "$SCRIPT_DIR/macos-start.sh" 2>/dev/null || true
echo "Execute permission restored on $APP_NAME."

# --------------------------------------------------------------------------
# 4. Clear macOS's download-quarantine flag so Gatekeeper doesn't refuse to
#    launch an app that isn't notarized by Apple. Applied to the whole app
#    folder - the native .dylib libraries inside it are quarantined too, and
#    each would otherwise be blocked when the app first loads it.
# --------------------------------------------------------------------------
if command -v xattr >/dev/null 2>&1; then
    xattr -dr com.apple.quarantine "$APP_DIR" 2>/dev/null || true
    echo "Quarantine flag cleared."
else
    echo "Note: 'xattr' was not found - if macOS refuses to open the app,"
    echo "allow it under System Settings > Privacy & Security > 'Open Anyway'."
fi

# --------------------------------------------------------------------------
# 5. OCR native libraries. Encounter/catch detection uses Tesseract OCR, and
#    the TesseractOCR package doesn't ship working macOS natives (same story
#    as Linux - see linux-fix-tesseract-ocr.sh and MIGRATION_GUIDE.md
#    §17/§18). Its loader searches an "x64" subfolder of the app folder for
#    these exact (deliberately odd) file names:
#        x64/libleptonica-1.85.0.dll.dylib
#        x64/libtesseract55.dll.dylib
#    Homebrew's tesseract + leptonica provide the real libraries; symlinks
#    under the names the loader wants bridge the two. The app itself starts
#    and runs fine without this - only OCR-based detection needs it.
# --------------------------------------------------------------------------
# §121: which ARCHITECTURE the libraries have to be.
#
# This is not decoration. A Mac can have Homebrew twice over - the Apple
# Silicon one at /opt/homebrew holding arm64 libraries, the Intel one at
# /usr/local holding x86_64 - and running the osx-x64 build on Apple Silicon
# puts an x86_64 process next to an arm64 Homebrew. The old lookup took the
# first match it found by name, which on that machine is the arm64 dylib, and
# linked it. The process then refuses to load it, and a dylib of the wrong
# architecture fails exactly as invisibly as one that is not there: OCR never
# starts, and nothing in the message says why.
#
# So work out what the app needs and check every candidate against it. The
# folder name is authoritative when there is one, because it is what the
# publish put the binary in; otherwise ask the binary itself.
APP_ARCH=""
case "$APP_DIR" in
    *osx-arm64*) APP_ARCH="arm64" ;;
    *osx-x64*)   APP_ARCH="x86_64" ;;
esac

if [ -z "$APP_ARCH" ] && command -v lipo >/dev/null 2>&1; then
    APP_ARCH="$(lipo -archs "$APP_DIR/$APP_NAME" 2>/dev/null | tr ' ' '\n' | head -1)"
fi

[ -n "$APP_ARCH" ] || APP_ARCH="$MACHINE_ARCH"

BREW_PREFIXES="$(brew --prefix 2>/dev/null) /opt/homebrew /usr/local"

lib_matches_arch() {
    # $1 = dylib path, $2 = architecture the app needs.
    [ -e "$1" ] || return 1

    # No lipo means no way to check. Do not let a missing developer tool
    # block a setup that would otherwise work - fall back to the old
    # behaviour of trusting the name.
    command -v lipo >/dev/null 2>&1 || return 0

    lipo -archs "$1" 2>/dev/null | tr ' ' '\n' | grep -qx "$2"
}

find_brew_lib() {
    # $1 = library base name glob, e.g. "libtesseract*.dylib"
    # $2 = required architecture
    #
    # Every match is considered, not just the first: the first by name is
    # often the wrong architecture on a machine with both Homebrews, and
    # skipping past it is the whole point. An unmatched glob stays literal
    # and is filtered out by the -e test inside lib_matches_arch.
    for prefix in $BREW_PREFIXES; do
        [ -n "$prefix" ] && [ -d "$prefix/lib" ] || continue
        for candidate in "$prefix"/lib/$1; do
            lib_matches_arch "$candidate" "$2" || continue
            echo "$candidate"
            return 0
        done
    done
    return 1
}

# §121: what IS on this machine, printed when the lookup comes up empty.
#
# Straight from the Linux side (§118): a library present under a name we
# failed to anticipate produced output identical to a package that was never
# installed, and those two need opposite responses from whoever reads it
# next. The same trap is set here twice over, by name AND by architecture,
# so a failure says what it looked for and what it found instead.
show_ocr_library_evidence() {
    echo ""
    echo "  Looked for libtesseract*.dylib and liblept*.dylib built for"
    echo "  $APP_ARCH, in: $BREW_PREFIXES"
    echo ""
    echo "  What is actually there:"

    found_any=0
    for prefix in $BREW_PREFIXES; do
        [ -n "$prefix" ] && [ -d "$prefix/lib" ] || continue
        for candidate in "$prefix"/lib/libtesseract*.dylib "$prefix"/lib/liblept*.dylib; do
            [ -e "$candidate" ] || continue
            found_any=1
            arch_desc="$(lipo -archs "$candidate" 2>/dev/null)"
            [ -n "$arch_desc" ] || arch_desc="architecture unknown"
            echo "    $candidate  [$arch_desc]"
        done
    done

    if [ "$found_any" -eq 0 ]; then
        echo "    (nothing at all - so the packages really are not installed)"
    fi
}

# §121: the one thing this script must not do quietly.
#
# Without OCR the app still starts, the window opens, the database works and
# every screen looks healthy - and not one encounter is ever recorded. That
# symptom is indistinguishable from the app being broken, and on Linux it
# cost days of looking at capture, at Wayland, at permissions, at everything
# except the setup step that had printed a mild note and carried on. So this
# says it once, loudly, in terms of what the reader is about to experience.
ocr_not_set_up_banner() {
    echo ""
    echo "  ################################################################"
    echo "  #  OCR IS NOT SET UP - THE APP WILL RUN AND COUNT NOTHING      #"
    echo "  ################################################################"
    echo ""
    echo "  Worth reading before you go hunting. The tracker will open and"
    echo "  everything will look fine. No encounter will ever be recorded,"
    echo "  because nothing can be read from the game window."
}

if command -v brew >/dev/null 2>&1; then
    echo ""
    echo "Looking for the OCR libraries ($APP_ARCH)..."

    TESSERACT_LIB="$(find_brew_lib 'libtesseract*.dylib' "$APP_ARCH" || true)"
    LEPTONICA_LIB="$(find_brew_lib 'liblept*.dylib' "$APP_ARCH" || true)"

    if [ -z "$TESSERACT_LIB" ] || [ -z "$LEPTONICA_LIB" ]; then
        echo ""
        echo "Tesseract/Leptonica were not found - installing via Homebrew so"
        echo "encounter detection (OCR) can work. This may take a minute..."

        brew install tesseract leptonica
        BREW_EXIT=$?

        # §121: this used to be "|| true", which threw away the one fact
        # worth keeping. A failed install followed by a failed lookup reads
        # as "not found" when the truth is "could not be fetched", and the
        # advice for those two differs.
        if [ "$BREW_EXIT" -ne 0 ]; then
            echo ""
            echo "  Homebrew exited with code $BREW_EXIT - the install did not"
            echo "  complete. Whatever it printed above is the reason."
        fi

        TESSERACT_LIB="$(find_brew_lib 'libtesseract*.dylib' "$APP_ARCH" || true)"
        LEPTONICA_LIB="$(find_brew_lib 'liblept*.dylib' "$APP_ARCH" || true)"
    fi

    if [ -n "$TESSERACT_LIB" ] && [ -n "$LEPTONICA_LIB" ]; then
        mkdir -p "$APP_DIR/x64"
        ln -sf "$LEPTONICA_LIB" "$APP_DIR/x64/libleptonica-1.85.0.dll.dylib"
        ln -sf "$TESSERACT_LIB" "$APP_DIR/x64/libtesseract55.dll.dylib"
        # Harmless top-level fallback copies, same as the Linux script makes.
        ln -sf "$LEPTONICA_LIB" "$APP_DIR/libleptonica-1.85.0.dll.dylib"
        ln -sf "$TESSERACT_LIB" "$APP_DIR/libtesseract55.dll.dylib"

        # §121: a symlink is created just as happily when its target is
        # gone, and a dangling one fails at load time as the same missing-
        # library error this whole step exists to prevent. Creating it is
        # not the same as it working, so check.
        OCR_LINKS_OK=1
        for link in "$APP_DIR/x64/libleptonica-1.85.0.dll.dylib" \
                    "$APP_DIR/x64/libtesseract55.dll.dylib"; do
            if [ ! -e "$link" ]; then
                OCR_LINKS_OK=0
                echo ""
                echo "  ERROR: $link does not resolve to a real file."
            fi
        done

        if [ "$OCR_LINKS_OK" -eq 1 ]; then
            echo "OCR libraries linked ($APP_ARCH):"
            echo "  x64/libtesseract55.dll.dylib -> $TESSERACT_LIB"
            echo "  x64/libleptonica-1.85.0.dll.dylib -> $LEPTONICA_LIB"
        else
            ocr_not_set_up_banner
            echo ""
            echo "  The libraries were found but the links do not resolve, which"
            echo "  usually means Homebrew moved or was uninstalled since. Try:"
            echo "      brew reinstall tesseract leptonica"
            echo "  and run this script again."
            show_ocr_library_evidence
        fi
    else
        ocr_not_set_up_banner
        echo ""
        echo "  Fix it with:"
        echo "      brew install tesseract leptonica"
        echo "  then run this script again."

        # §121: on Apple Silicon an x86_64 build needs the INTEL Homebrew,
        # which is a separate installation at /usr/local. Saying so here saves
        # the reader discovering it from a dylib architecture error, or never.
        if [ "$APP_ARCH" = "x86_64" ] && [ "$MACHINE_ARCH" = "arm64" ]; then
            echo ""
            echo "  NOTE: this is the Intel build on an Apple Silicon Mac, so it"
            echo "  needs x86_64 libraries - the Apple Silicon Homebrew at"
            echo "  /opt/homebrew cannot provide them. Either install the Intel"
            echo "  Homebrew under /usr/local (arch -x86_64 ...), or simply use"
            echo "  the osx-arm64 build instead, which is the better answer."
        fi

        show_ocr_library_evidence
    fi
else
    ocr_not_set_up_banner
    echo ""
    echo "  Homebrew is not installed, and it is where the OCR libraries come"
    echo "  from. Install it from https://brew.sh and run this script again."
fi

# --------------------------------------------------------------------------
# 6. macOS privacy permissions (TCC). Window capture needs TWO separate
#    grants, and missing them is the single most likely reason the app runs
#    perfectly but never detects a single encounter:
#
#      Accessibility     - osascript drives System Events to find out where
#                          the PRO client's window sits on screen.
#      Screen Recording  - screencapture then grabs that region of the screen.
#
#    THE PART THAT CATCHES EVERYONE OUT: this app is published as a bare
#    executable rather than a .app bundle, so it has no identity of its own
#    for macOS to hang permissions on. macOS attributes them to whatever
#    LAUNCHED it - which is this Terminal. So in System Settings you are
#    looking for "Terminal" (or iTerm2, or whichever you used) in those two
#    lists, NOT "ProTrackerDatabase.Avalonia". Two consequences worth knowing:
#
#      * A grant does NOT apply to an already-running process. After granting,
#        QUIT Terminal completely (Cmd-Q, not just closing the window) and
#        reopen it, then run this script again.
#      * Always launch through this script, from the terminal you granted.
#        Launching some other way makes a different app the responsible one,
#        and the permissions will look granted while not applying.
#
#    The probes below exist to trigger both prompts NOW, while this script is
#    here to explain them - rather than silently, mid-hunt, when the only
#    symptom is that nothing is ever detected.
# --------------------------------------------------------------------------
echo ""
echo "Checking the two macOS permissions window capture needs..."
echo "(If a permission box appears, click Allow / Open System Settings.)"

ACCESSIBILITY_OK=0
SCREEN_OK=0

# Accessibility: a denied Apple event fails cleanly with error -1743, so this
# one can actually be detected rather than guessed at.
if command -v osascript >/dev/null 2>&1; then
    if osascript -e 'tell application "System Events" to get name of first process' >/dev/null 2>&1; then
        ACCESSIBILITY_OK=1
    fi
fi

# Screen Recording: there is no shell-level way to read a TCC grant, so this
# takes a tiny throwaway capture. A hard failure proves it is denied; success
# only proves screencapture ran, which is why the wording below stays honest
# about that rather than claiming the grant is definitely in place.
PROBE_PNG="${TMPDIR:-/tmp}/protracker-capture-probe.png"
rm -f "$PROBE_PNG" 2>/dev/null || true

if command -v screencapture >/dev/null 2>&1; then
    screencapture -x -R 0,0,4,4 "$PROBE_PNG" >/dev/null 2>&1 || true
    [ -s "$PROBE_PNG" ] && SCREEN_OK=1
    rm -f "$PROBE_PNG" 2>/dev/null || true
fi

echo ""
if [ "$ACCESSIBILITY_OK" -eq 1 ]; then
    echo "  Accessibility    : looks granted."
else
    echo "  Accessibility    : NOT granted - the app will not be able to find"
    echo "                     the PRO client window."
    echo "                     System Settings > Privacy & Security >"
    echo "                     Accessibility > turn ON your terminal app."
fi

if [ "$SCREEN_OK" -eq 1 ]; then
    echo "  Screen Recording : screencapture ran. If encounters still never"
    echo "                     detect, this is the one to check first -"
    echo "                     a denied grant can still produce an image, just"
    echo "                     without any other app's window in it."
else
    echo "  Screen Recording : NOT working - screencapture produced nothing."
    echo "                     System Settings > Privacy & Security >"
    echo "                     Screen Recording > turn ON your terminal app."
fi

if [ "$ACCESSIBILITY_OK" -eq 0 ] || [ "$SCREEN_OK" -eq 0 ]; then
    echo ""
    echo "  After granting: QUIT your terminal completely (Cmd-Q) and reopen"
    echo "  it, then run this script again. A permission granted to a running"
    echo "  terminal does not reach the app it already launched."
    echo ""
    echo "  The app will still start now - the tracker window, the database,"
    echo "  the Pokemon reference data and everything that does not need a"
    echo "  screenshot all work. Only encounter detection is affected."
fi

# --------------------------------------------------------------------------
# 7. Launch, and say where the log lives - if anything goes wrong from here,
#    that log (plus this terminal's output) is what to send the developer.
# --------------------------------------------------------------------------
echo ""
echo "Starting Pro Tracker..."
echo "(If it fails, send the newest file from:"
echo "  ~/Library/Application Support/ProTracker/Logs/  - plus anything printed below.)"
echo ""

cd "$APP_DIR" && exec "./$APP_NAME"
