#!/bin/bash
# ============================================================================
# Pro Tracker - Linux one-command start
# ============================================================================
#
# The one file a tester should need to run, full stop. This lives OUTSIDE
# the linux-x64 folder (right next to it, and next to this project's
# README-Linux-and-macOS-Setup.txt) - unlike linux-setup.sh and
# linux-fix-tesseract-ocr.sh, which live INSIDE linux-x64 (the .csproj
# copies them there automatically for every Linux publish - see
# ProTrackerDatabase.Avalonia.csproj). This script can't be one of those
# auto-copied files itself, since its whole job is to sit one level ABOVE
# that folder, not inside it - see MIGRATION_GUIDE.md.
#
# What it does, in order:
#   1. Finds the linux-x64 folder next to this script (falls back to
#      searching immediate subfolders if it's ever renamed).
#   2. Runs linux-setup.sh from inside it. That script already handles
#      system packages, the .NET SDK, building/locating the app, AND
#      running linux-fix-tesseract-ocr.sh against the result (see that
#      script's own "step 6") - so this one call covers both of the
#      existing scripts, not just one of them.
#   3. Best-effort: silences the Linux/GNOME "bell" sound. Never fails the
#      rest of the script if it can't - not every desktop has the tools
#      this uses, and this is a nice-to-have, not a requirement.
#   4. Launches the app.
#
# Usage:
#   bash linux-start.sh
#
# (Use "bash linux-start.sh" rather than "./linux-start.sh" the first time -
# same reason as linux-setup.sh: a zip made on Windows and unzipped on
# Linux can lose this file's execute permission. This script tries to fix
# that for next time near the top, but the very first run still needs
# "bash" in front of it.)
#
# This script is specifically the "I was handed a zip with a linux-x64
# folder in it" tester path. Building from a source checkout, or working
# directly on a machine that doesn't have a pre-built linux-x64 folder yet,
# should still use linux-setup.sh directly (it already handles both cases -
# see its own comments).

set -u
shopt -s nullglob

APP_NAME="ProTrackerDatabase.Avalonia"   # must match linux-setup.sh's own APP_NAME

step() { echo ""; echo "==> $1"; }
ok()   { echo "    OK: $1"; }
warn() { echo "    Warning: $1"; }
fail() { echo "    ERROR: $1"; }

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

# Best-effort: restore the execute bit for next time, so a future
# "./linux-start.sh" works without needing "bash" in front of it. Not
# needed for THIS run either way (we're already running under bash), so a
# failure here (e.g. read-only filesystem) is not worth mentioning.
chmod +x "$SCRIPT_DIR/linux-start.sh" 2>/dev/null || true

echo "Pro Tracker Linux launcher"

# ============================================================================
# 1. Find the linux-x64 folder
# ============================================================================
step "Locating the app folder"

APP_DIR=""
if [ -f "$SCRIPT_DIR/linux-x64/linux-setup.sh" ]; then
    APP_DIR="$SCRIPT_DIR/linux-x64"
else
    warn "No linux-x64 folder found next to this script - looking for a"
    warn "renamed one instead..."
    for candidate in "$SCRIPT_DIR"/*/; do
        if [ -f "${candidate}linux-setup.sh" ]; then
            APP_DIR="${candidate%/}"
            break
        fi
    done
fi

if [ -z "$APP_DIR" ]; then
    fail "Could not find the app folder (looked for a folder called"
    fail "'linux-x64' next to this script, or failing that, any folder next"
    fail "to it that contains linux-setup.sh)."
    fail "Make sure linux-start.sh sits right next to the linux-x64 folder"
    fail "from your download - not inside it, and not moved somewhere else"
    fail "on its own."
    exit 1
fi

ok "Found: $APP_DIR"

# ============================================================================
# 2. Run setup (system packages, .NET SDK, build/locate, OCR native-lib fix)
# ============================================================================
step "Running one-time setup"
echo "    (this also runs linux-fix-tesseract-ocr.sh as part of its own"
echo "    steps below - one call here covers both existing scripts)"

if ! bash "$APP_DIR/linux-setup.sh" "$APP_DIR"; then
    echo ""
    fail "Setup did not finish successfully - see the errors above."
    fail "Fix whatever it reported, then run this script again."
    exit 1
fi

# ============================================================================
# 3. Best-effort: silence the Linux/GNOME bell sound
# ============================================================================
step "Disabling the system bell sound"

DISABLED_ANYTHING=0

# The classic X11 "PC speaker" bell (still relevant under XWayland, which is
# how most Wayland desktops show X11 apps - see README-Linux-and-macOS-
# Setup.txt on why that matters for this app already).
if command -v xset >/dev/null 2>&1 && [ -n "${DISPLAY:-}" ]; then
    if xset b off >/dev/null 2>&1; then
        ok "Disabled the X11 bell (xset b off)."
        DISABLED_ANYTHING=1
    fi
fi

# GNOME's own "audible bell" setting (what GNOME actually wires XBell()/
# window-urgency signals to, separately from raw X11 above).
if command -v gsettings >/dev/null 2>&1; then
    if gsettings set org.gnome.desktop.wm.preferences audible-bell false >/dev/null 2>&1; then
        ok "Disabled GNOME's audible-bell setting."
        DISABLED_ANYTHING=1
    fi

    # GNOME's general UI/event sound effects (dialog beeps, notification
    # dings, etc., via libcanberra/PulseAudio) - a separate mechanism from
    # the WM bell above, and plausibly the actual source of the sound if
    # what's playing is a "dialog-warning"-type sound rather than a literal
    # bell. Included for the same reason - without being able to hear it
    # ourselves, covering both is more likely to actually silence it than
    # guessing which one it is.
    if gsettings set org.gnome.desktop.sound event-sounds false >/dev/null 2>&1; then
        ok "Disabled GNOME's event sound effects."
        DISABLED_ANYTHING=1
    fi
fi

if [ "$DISABLED_ANYTHING" -eq 0 ]; then
    warn "Could not find a way to disable the bell on this desktop (this step"
    warn "only knows how to do it on X11 and GNOME). If you hear a beep when"
    warn "a tracking error banner shows up, that's your terminal/desktop's"
    warn "own system sound, not this app - it's just noise and can be ignored."
fi

# ============================================================================
# 4. Launch
# ============================================================================
step "Starting Pro Tracker"

EXE_PATH="$APP_DIR/$APP_NAME"

if [ ! -f "$EXE_PATH" ]; then
    fail "Expected executable not found at $EXE_PATH."
    fail "Setup reported success above, but the exe isn't where it should"
    fail "be - something unexpected happened. Check the setup output above."
    exit 1
fi

# Safety net - linux-setup.sh already does this too, but costs nothing to
# double-check right before actually launching.
chmod +x "$EXE_PATH" 2>/dev/null || true

echo "    Launching $APP_NAME ..."
echo ""
cd "$APP_DIR"
exec "./$APP_NAME" "$@"
