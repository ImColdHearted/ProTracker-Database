#!/bin/bash
# ============================================================================
# Pro Tracker - Linux one-time setup / build script
# ============================================================================
#
# Run this on the Linux machine you're testing on. It handles everything
# needed to get the app running from a clean VM in one pass, instead of the
# usual back-and-forth of: try running it, hit a missing-library error, look
# up which package to install, install it by hand, try again, hit the NEXT
# missing thing, repeat.
#
# What it does, in order:
#   1. Detects your distro's package manager (apt/dnf/yum/pacman/zypper/apk,
#      plus the rpm-ostree "atomic" Fedora variants) - see /etc/os-release.
#   2. Installs the Linux-specific system packages this app needs that a
#      normal desktop install usually doesn't already have:
#        - wmctrl                          (finds the PRO client window)
#        - imagemagick                     (captures the window as an image)
#        - libtesseract-dev/libleptonica-dev, or your distro's equivalent
#                                           (OCR engine - see the comment
#                                            block in linux-fix-tesseract-ocr.sh
#                                            for why these need a separate,
#                                            distro-specific step at all)
#   3. Checks for the .NET 10 SDK and installs it (to your own home
#      directory, no root needed) via Microsoft's official install script if
#      it's missing - this project can't be built without it.
#   4. If run from a source checkout (finds the .csproj), builds a real
#      Linux build: `dotnet publish -r linux-x64 -c Release`. If run from an
#      already-published folder instead (e.g. you were handed a zip), it
#      skips straight to the next step against that folder.
#   5. Runs linux-fix-tesseract-ocr.sh against the build output - a separate,
#      already-existing script that works around a native-library-loading
#      bug in the OCR package itself (see that script's own comments/
#      MIGRATION_GUIDE.md for the full story). Requires step 2's packages to
#      already be in place, which is exactly the order this script runs them.
#   6. Makes sure the built executable actually has the execute bit set (a
#      zip made on Windows and unzipped on Linux loses this, which shows up
#      as a confusing "Permission denied" that has nothing to do with any of
#      the above) and prints the exact command to run the app.
#
# Usage:
#   ./linux-setup.sh                  # operates on the current directory
#   ./linux-setup.sh /path/to/project # or a source checkout / publish folder
#
# Safe to run more than once - every step checks whether it's already done
# before doing it again.

set -u

APP_NAME="ProTrackerDatabase.Avalonia"
TARGET_DIR="${1:-$(pwd)}"
# §116: TARGET_DIR is where we were AIMED; SCRIPT_DIR is where we ARE.
# They differ whenever this is run by path from somewhere else, and the
# companion fix script may be beside either one - see the search below.
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DOTNET_CHANNEL="10.0"

step() { echo ""; echo "==> $1"; }
ok()   { echo "    OK: $1"; }
warn() { echo "    Warning: $1"; }
fail() { echo "    ERROR: $1"; }

if [ ! -d "$TARGET_DIR" ]; then
    fail "'$TARGET_DIR' is not a directory."
    exit 1
fi
TARGET_DIR="$(cd "$TARGET_DIR" && pwd)"

echo "Pro Tracker Linux setup"
echo "Operating on: $TARGET_DIR"

# ============================================================================
# 1. Distro / package manager detection
# ============================================================================
step "Detecting Linux distro / package manager"

PACKAGE_MANAGER=""

if [ -f /run/ostree-booted ]; then
    PACKAGE_MANAGER="rpm-ostree"
elif [ -f /etc/os-release ]; then
    . /etc/os-release
    ID_LIKE_LOWER=$(printf '%s' "${ID_LIKE:-}" | tr '[:upper:]' '[:lower:]')
    ID_LOWER=$(printf '%s' "${ID:-}" | tr '[:upper:]' '[:lower:]')

    case "$ID_LOWER $ID_LIKE_LOWER" in
        *debian*|*ubuntu*) PACKAGE_MANAGER="apt" ;;
        *fedora*|*rhel*|*centos*)
            command -v dnf >/dev/null 2>&1 && PACKAGE_MANAGER="dnf" || PACKAGE_MANAGER="yum" ;;
        *arch*)  PACKAGE_MANAGER="pacman" ;;
        *suse*)  PACKAGE_MANAGER="zypper" ;;
        *alpine*) PACKAGE_MANAGER="apk" ;;
    esac
fi

if [ -z "$PACKAGE_MANAGER" ]; then
    for candidate in apt dnf yum pacman zypper apk; do
        if command -v "$candidate" >/dev/null 2>&1; then
            PACKAGE_MANAGER="$candidate"
            break
        fi
    done
fi

if [ -z "$PACKAGE_MANAGER" ]; then
    warn "Could not detect a known package manager. Skipping automatic package"
    warn "install - you'll need to install these yourself: wmctrl, imagemagick,"
    warn "libtesseract-dev (or tesseract-devel), libleptonica-dev (or leptonica-devel)."
else
    ok "Detected: $PACKAGE_MANAGER"
fi

SUDO=""
if [ "$(id -u)" -ne 0 ]; then
    if command -v sudo >/dev/null 2>&1; then
        SUDO="sudo"
    else
        warn "Not running as root and 'sudo' is not installed - package installs"
        warn "below will likely fail. Re-run as root if that happens."
    fi
fi

# ============================================================================
# 2. Session type (X11 vs Wayland) - informational only
# ============================================================================
step "Checking display server type"

SESSION_TYPE="${XDG_SESSION_TYPE:-unknown}"
if [ "$SESSION_TYPE" = "wayland" ]; then
    warn "You're on a native Wayland session (XDG_SESSION_TYPE=wayland)."
    warn "Window finding/capture in this app uses wmctrl + import, which only see"
    warn "X11 windows - this works fine under XWayland (most desktops run this"
    warn "automatically for compatibility, so this is very likely a non-issue),"
    warn "but a Wayland-native PRO client window would not be visible to it."
    warn "If window detection doesn't work later, this is the first thing to check."
elif [ "$SESSION_TYPE" = "x11" ]; then
    ok "X11 session detected - window capture should work normally."
else
    warn "Could not determine session type (XDG_SESSION_TYPE is unset) - continuing anyway."
fi

# ============================================================================
# 2b. Sound player (§148) - informational only
# ============================================================================
# The Since Form / Since Shiny alert sounds are plain .wav files played
# through whichever of these the tracker finds first on the PATH. Nothing is
# installed here - the alerts are optional - but a machine with none of them
# will hunt in silence, and the tracker's log will say so.
step "Checking for a sound player (optional - alert sounds)"

SOUND_PLAYER=""
for candidate in paplay pw-play aplay ffplay mpv play; do
    if command -v "$candidate" >/dev/null 2>&1; then
        SOUND_PLAYER="$candidate"
        break
    fi
done

if [ -n "$SOUND_PLAYER" ]; then
    ok "$SOUND_PLAYER found - the Since Form / Since Shiny alert sounds will play through it."
else
    warn "No sound player found (looked for paplay, pw-play, aplay, ffplay, mpv, play)."
    warn "The tracker runs fine without one, but alert sounds will be silent."
    warn "To hear them, install one: pulseaudio-utils (paplay), pipewire (pw-play)"
    warn "or alsa-utils (aplay) - whichever matches your desktop's audio system."
fi

# ============================================================================
# 3. Install system packages (wmctrl, imagemagick, tesseract/leptonica dev)
# ============================================================================
step "Installing required system packages"

install_packages() {
    case "$PACKAGE_MANAGER" in
        apt)
            $SUDO apt-get update && \
            $SUDO apt-get install -y wmctrl imagemagick libtesseract-dev libleptonica-dev
            ;;
        dnf)
            $SUDO dnf install -y wmctrl ImageMagick tesseract-devel leptonica-devel
            ;;
        yum)
            $SUDO yum install -y wmctrl ImageMagick tesseract-devel leptonica-devel
            ;;
        pacman)
            $SUDO pacman -Sy --noconfirm wmctrl imagemagick tesseract leptonica
            ;;
        zypper)
            $SUDO zypper --non-interactive install wmctrl ImageMagick tesseract-ocr-devel leptonica-devel
            ;;
        apk)
            warn "Alpine detected - installing packages, but this app's .NET publish"
            warn "targets glibc. Running it on musl-based Alpine at all needs a"
            warn "separate linux-musl-x64 publish, which this script does not build."
            $SUDO apk add --no-cache wmctrl imagemagick tesseract-ocr-dev leptonica-dev
            ;;
        rpm-ostree)
            fail "This is a Fedora Atomic / immutable system - packages can't be"
            fail "installed live. Run this yourself, reboot, then re-run this script:"
            echo ""
            echo "    rpm-ostree install wmctrl ImageMagick tesseract-devel leptonica-devel"
            echo ""
            return 1
            ;;
        *)
            return 1
            ;;
    esac
}

if [ -n "$PACKAGE_MANAGER" ]; then
    if install_packages; then
        ok "System packages installed (or already present)."
    else
        warn "Automatic package install did not complete - see messages above."
        warn "You may need to install wmctrl, imagemagick, libtesseract-dev, and"
        warn "libleptonica-dev (or your distro's equivalent names) by hand."
    fi
fi

# ============================================================================
# 4. .NET SDK check / install
# ============================================================================
step "Checking for the .NET $DOTNET_CHANNEL SDK"

HAVE_DOTNET10=0
if command -v dotnet >/dev/null 2>&1; then
    if dotnet --list-sdks 2>/dev/null | grep -q "^${DOTNET_CHANNEL%.*}\."; then
        HAVE_DOTNET10=1
        ok "Found: $(dotnet --list-sdks | grep "^${DOTNET_CHANNEL%.*}\." | tail -1)"
    else
        warn "'dotnet' is installed, but no ${DOTNET_CHANNEL%.*}.x SDK was found:"
        dotnet --list-sdks 2>/dev/null | sed 's/^/      /'
    fi
fi

if [ "$HAVE_DOTNET10" -eq 0 ]; then
    echo "    Installing .NET $DOTNET_CHANNEL SDK to \$HOME/.dotnet via Microsoft's"
    echo "    official install script (no root needed)..."

    INSTALL_SCRIPT="$(mktemp)"
    if command -v curl >/dev/null 2>&1; then
        curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$INSTALL_SCRIPT"
    elif command -v wget >/dev/null 2>&1; then
        wget -q https://dot.net/v1/dotnet-install.sh -O "$INSTALL_SCRIPT"
    else
        fail "Neither curl nor wget is available - can't download the .NET installer."
        fail "Install curl or wget, or install the .NET $DOTNET_CHANNEL SDK yourself, then re-run."
        exit 1
    fi

    chmod +x "$INSTALL_SCRIPT"
    "$INSTALL_SCRIPT" --channel "$DOTNET_CHANNEL"
    rm -f "$INSTALL_SCRIPT"

    export PATH="$HOME/.dotnet:$PATH"

    if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks 2>/dev/null | grep -q "^${DOTNET_CHANNEL%.*}\."; then
        fail ".NET $DOTNET_CHANNEL SDK install did not complete successfully."
        exit 1
    fi

    ok "Installed. Add this to your shell profile (~/.bashrc etc.) to use 'dotnet'"
    echo "    in new terminals without re-running this script:"
    echo "        export PATH=\"\$HOME/.dotnet:\$PATH\""
fi

# ============================================================================
# 5. Build (source checkout) or locate (already-published) the app
# ============================================================================
step "Locating the project"

CSPROJ=""
PUBLISH_DIR=""

if [ -f "$TARGET_DIR/$APP_NAME.csproj" ]; then
    CSPROJ="$TARGET_DIR/$APP_NAME.csproj"
elif [ -x "$TARGET_DIR/$APP_NAME" ] || [ -f "$TARGET_DIR/$APP_NAME" ]; then
    PUBLISH_DIR="$TARGET_DIR"
else
    FOUND_CSPROJ="$(find "$TARGET_DIR" -maxdepth 4 -name "$APP_NAME.csproj" 2>/dev/null | head -1)"
    FOUND_EXE="$(find "$TARGET_DIR" -maxdepth 4 -name "$APP_NAME" -type f 2>/dev/null | head -1)"

    if [ -n "$FOUND_CSPROJ" ]; then
        CSPROJ="$FOUND_CSPROJ"
    elif [ -n "$FOUND_EXE" ]; then
        PUBLISH_DIR="$(dirname "$FOUND_EXE")"
    fi
fi

if [ -n "$CSPROJ" ]; then
    ok "Found source project: $CSPROJ"
    PROJECT_DIR="$(dirname "$CSPROJ")"

    step "Building (dotnet publish -r linux-x64 -c Release)"
    echo "    This can take a few minutes the first time."

    (cd "$PROJECT_DIR" && dotnet publish "$CSPROJ" -r linux-x64 -c Release --self-contained true)
    BUILD_EXIT=$?

    if [ "$BUILD_EXIT" -ne 0 ]; then
        fail "dotnet publish failed (exit code $BUILD_EXIT) - see the output above."
        exit 1
    fi

    PUBLISH_DIR="$(find "$PROJECT_DIR/bin" -type d -path "*linux-x64/publish" 2>/dev/null | head -1)"

    if [ -z "$PUBLISH_DIR" ]; then
        fail "Build succeeded but the linux-x64 publish folder could not be found under"
        fail "$PROJECT_DIR/bin - look for it manually (bin/Release/net10.0/linux-x64/publish)."
        exit 1
    fi

    ok "Published to: $PUBLISH_DIR"
elif [ -n "$PUBLISH_DIR" ]; then
    ok "Found an already-published build: $PUBLISH_DIR"
else
    fail "Could not find '$APP_NAME.csproj' (a source checkout) or a"
    fail "'$APP_NAME' executable (an already-published build) in or under"
    fail "$TARGET_DIR."
    fail "Run this script from that folder, or pass its path as an argument:"
    fail "    ./linux-setup.sh /path/to/Pro_Tracker"
    exit 1
fi

# ============================================================================
# 6. Fix the OCR native-library loading bug
# ============================================================================
step "Fixing Tesseract/Leptonica native library loading"

# §116: look in three places, not one, and stop pretending a miss is
# survivable.
#
# This used to check only "$PUBLISH_DIR/linux-fix-tesseract-ocr.sh". That is
# right for a publish folder - the .csproj copies the script into every Linux
# publish - and wrong for a zip that carries the scripts at its root, one
# level above the app. A real download looked like this:
#
#     ProTrackerDatabase-LinuxX64/
#         linux-x64/                    <- PUBLISH_DIR resolves here, correctly
#         linux-fix-tesseract-ocr.sh    <- but the script is HERE
#         linux-setup.sh
#
# The lookup missed. The old code warned rather than failing, then ran on and
# printed its usual closing "you can now start the app" instructions, so
# setup read as a success. OCR had not been touched, and the app died on its
# first scan with DllNotFoundException for libleptonica - which is how a
# whole evening went into capture and Wayland before anyone suspected setup.
#
# Linking those libraries is the one job this script cannot skip, so not
# finding the script that does it is now fatal.
FIX_SCRIPT=""

for CANDIDATE in \
    "$PUBLISH_DIR/linux-fix-tesseract-ocr.sh" \
    "$SCRIPT_DIR/linux-fix-tesseract-ocr.sh" \
    "$TARGET_DIR/linux-fix-tesseract-ocr.sh"
do
    if [ -f "$CANDIDATE" ]; then
        FIX_SCRIPT="$CANDIDATE"
        break
    fi
done

if [ -z "$FIX_SCRIPT" ]; then
    FIX_SCRIPT="$(find "$TARGET_DIR" -maxdepth 4 -name "linux-fix-tesseract-ocr.sh" -type f 2>/dev/null | head -1)"
fi

if [ -n "$FIX_SCRIPT" ]; then
    ok "Using $FIX_SCRIPT"
    chmod +x "$FIX_SCRIPT"
    # Hand it the app folder explicitly. It can work this out for itself now
    # (see §116 there), but it should never have to guess while the
    # caller already knows - and if the two ever disagreed, the symlinks
    # would land somewhere nothing reads.
    "$FIX_SCRIPT" "$PUBLISH_DIR"
else
    fail "linux-fix-tesseract-ocr.sh could not be found. Looked in:"
    fail "    $PUBLISH_DIR"
    fail "    $SCRIPT_DIR"
    fail "    $TARGET_DIR (and up to 4 levels beneath it)"
    echo ""
    fail "Without it the OCR native libraries are never linked, so tracking"
    fail "would fail on its first scan with a DllNotFoundException for"
    fail "libleptonica. Stopping here rather than finishing as though setup"
    fail "had worked."
    echo ""
    fail "It ships in the same download as this script - if it is genuinely"
    fail "missing, download again rather than working around it."
    exit 1
fi

# ============================================================================
# 7. Make sure the executable bit survived the trip from Windows
# ============================================================================
step "Checking the executable bit"

EXE_PATH="$PUBLISH_DIR/$APP_NAME"

if [ -f "$EXE_PATH" ]; then
    if [ ! -x "$EXE_PATH" ]; then
        chmod +x "$EXE_PATH"
        ok "Set the missing execute bit on $APP_NAME (lost when this was zipped on"
        echo "    Windows and unzipped here - otherwise this shows up as a confusing"
        echo "    'Permission denied' with nothing to do with any library at all)."
    else
        ok "Executable bit already set."
    fi
else
    warn "Expected executable not found at $EXE_PATH - check the publish output above."
fi

# ============================================================================
# Done
# ============================================================================
step "Setup complete"
echo ""
echo "Run the app with:"
echo "    cd \"$PUBLISH_DIR\""
echo "    ./$APP_NAME"
echo ""
echo "If anything still fails, the log file (~/.local/share/ProTracker/Logs/)"
echo "and the in-app \"Report a Problem\" feature both capture more detail than"
echo "the terminal output usually will."
