#!/bin/bash
# Run this ONCE on your Linux machine. It can be run from anywhere: from the
# app's own folder, from a folder above it, or with that folder passed as an
# argument -
#
#     ./linux-fix-tesseract-ocr.sh                 # find the app from here
#     ./linux-fix-tesseract-ocr.sh /path/to/app    # or say where it is
#
# See "Where the app actually is" below for why that flexibility had to be
# added (§116) - the symlinks must land beside the EXECUTABLE, and this
# script used to assume it was already standing there.
#
# WHY THIS EXISTS: the TesseractOCR package this app uses for OCR claims to
# bundle native Linux binaries, but its own official documentation confirms
# it doesn't actually ship working ones - see
# https://github.com/Sicos1977/TesseractOCR/wiki/How-to-use-in-Docker-on-Linux
# On top of that, its native-library loader has a separate bug: it searches
# for filenames like "libleptonica-1.85.0.dll.so" (both .dll AND .so
# appended) instead of the real name a library would actually have.
#
# This script finds Tesseract/Leptonica wherever your system's package
# manager installed them, and creates symlinks under the exact (broken)
# names, in the exact "x64" subfolder, that the loader actually searches -
# the same approach the package's own official Docker guide uses, just for
# a regular Linux install instead of a container. (An earlier version of
# this script put the symlinks one folder level too shallow - directly in
# the publish folder instead of inside an "x64" subfolder of it, which the
# loader never actually checks. See MIGRATION_GUIDE.md.)
#
# Also symlinks libdl - a separate, unrelated need: the loader's own
# internal machinery for actually calling dlopen() wants libdl itself, and
# that specific lookup only ever checks the publish folder's top level, on
# a system where glibc no longer ships a standalone libdl.so file. See the
# comment further down and MIGRATION_GUIDE.md for how this was found.
#
# UPDATE: this now tries to install libtesseract-dev/libleptonica-dev itself
# (via whichever package manager it detects) before giving up - previously it
# only checked whether they were ALREADY installed and printed manual
# apt/dnf/pacman instructions otherwise, which is exactly the extra
# round-trip (install manually, re-run, hope it worked) this update removes.
# The manual instructions are still printed as a fallback if auto-install
# isn't possible (unknown package manager, no root, an atomic/immutable
# distro that needs a reboot first) - this script always either fixes things
# or tells you exactly what it couldn't do and why, never silently no-ops.

set -e

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APP_NAME="ProTrackerDatabase.Avalonia"

# ============================================================================
# Where the app actually is (§116)
# ============================================================================
# Every symlink below has to land next to the EXECUTABLE, because the folder
# TesseractOCR's loader searches is derived from the running app's own base
# directory - never from wherever this script happens to sit. Until §116
# the two were assumed to be the same folder, which is what this script's own
# header used to tell you to arrange by hand.
#
# That assumption held for a publish folder and broke for a zip. A real
# download had this shape:
#
#     ProTrackerDatabase-LinuxX64/
#         linux-x64/                    <- the app, and where the loader looks
#         linux-fix-tesseract-ocr.sh    <- this script, one level too high
#         linux-setup.sh
#
# Run from there, the old code created ProTrackerDatabase-LinuxX64/x64/ - a
# folder nothing ever reads - printed "Done", and left OCR exactly as broken
# as it found it. Doing the right thing in the wrong place is the worst
# failure mode a fix-it script can have, because it also spends the reader's
# belief that the problem is now solved.
#
# So resolve the app folder rather than assuming it:
#   1. an explicit path argument - what linux-setup.sh now passes
#   2. the executable sitting next to this script - the publish-folder case
#   3. a search beneath this script - the zip case drawn above
# and fall back to SCRIPT_DIR only after saying clearly that it is a guess.
APP_DIR=""

if [ "$#" -ge 1 ] && [ -n "${1:-}" ]; then
    if [ ! -d "$1" ]; then
        echo "ERROR: '$1' is not a directory." >&2
        exit 1
    fi
    APP_DIR="$(cd "$1" && pwd)"
elif [ -f "$SCRIPT_DIR/$APP_NAME" ]; then
    APP_DIR="$SCRIPT_DIR"
else
    FOUND_EXE="$(find "$SCRIPT_DIR" -maxdepth 3 -name "$APP_NAME" -type f 2>/dev/null | head -1)"
    if [ -n "$FOUND_EXE" ]; then
        APP_DIR="$(cd "$(dirname "$FOUND_EXE")" && pwd)"
    fi
fi

if [ -z "$APP_DIR" ]; then
    APP_DIR="$SCRIPT_DIR"
    echo ""
    echo "Warning: could not find '$APP_NAME' beside this script or in any"
    echo "folder beneath it, so the symlinks will be created in"
    echo "    $APP_DIR"
    echo "If that is not the folder holding the app, this will fix nothing."
    echo "Re-run it with the app's folder as an argument:"
    echo "    $0 /path/to/the/folder/containing/$APP_NAME"
    echo ""
elif [ "$APP_DIR" != "$SCRIPT_DIR" ]; then
    echo "Found the app in: $APP_DIR"
fi

# ============================================================================
# Distro / package manager detection
# ============================================================================
# Reads /etc/os-release (the standard systemd mechanism essentially every
# modern distro provides - Debian, Ubuntu, Fedora, RHEL, openSUSE, Arch,
# Alpine, and all their derivatives) rather than guessing from uname or
# checking for one specific distro by name, so this works the same way on a
# derivative distro (Mint, Pop!_OS, Rocky, Alma, Manjaro, EndeavourOS, etc.)
# as it does on the upstream one it's based on.
PACKAGE_MANAGER=""
IS_OSTREE=0

if [ -f /run/ostree-booted ]; then
    # Fedora Atomic / immutable variants (Silverblue, Kinoite, Bazzite, etc.) -
    # the base OS filesystem is read-only, so plain dnf is blocked even though
    # the distro is otherwise Fedora-based. Needs rpm-ostree + a reboot instead.
    IS_OSTREE=1
    PACKAGE_MANAGER="rpm-ostree"
elif [ -f /etc/os-release ]; then
    . /etc/os-release
    ID_LIKE_LOWER=$(printf '%s' "${ID_LIKE:-}" | tr '[:upper:]' '[:lower:]')
    ID_LOWER=$(printf '%s' "${ID:-}" | tr '[:upper:]' '[:lower:]')

    case "$ID_LOWER $ID_LIKE_LOWER" in
        *debian*|*ubuntu*)
            PACKAGE_MANAGER="apt" ;;
        *fedora*|*rhel*|*centos*)
            command -v dnf >/dev/null 2>&1 && PACKAGE_MANAGER="dnf" || PACKAGE_MANAGER="yum" ;;
        *arch*)
            PACKAGE_MANAGER="pacman" ;;
        *suse*)
            PACKAGE_MANAGER="zypper" ;;
        *alpine*)
            PACKAGE_MANAGER="apk" ;;
    esac
fi

# Fall back to checking which package-manager binary actually exists, in case
# /etc/os-release didn't match anything above (an ID/ID_LIKE this script
# doesn't recognize yet, on a distro that still uses one of these tools).
if [ -z "$PACKAGE_MANAGER" ]; then
    for candidate in apt dnf yum pacman zypper apk; do
        if command -v "$candidate" >/dev/null 2>&1; then
            PACKAGE_MANAGER="$candidate"
            break
        fi
    done
fi

SUDO=""
if [ "$(id -u)" -ne 0 ]; then
    if command -v sudo >/dev/null 2>&1; then
        SUDO="sudo"
    else
        echo "Warning: not running as root and 'sudo' was not found - package"
        echo "install steps below will likely fail. Re-run this script as root,"
        echo "or install sudo, if that happens."
        echo ""
    fi
fi

print_manual_instructions() {
    echo ""
    echo "Install the OCR libraries yourself with your distro's package manager,"
    echo "then run this script again:"
    echo ""
    echo "  Debian/Ubuntu:  sudo apt install libtesseract-dev libleptonica-dev"
    echo "  Fedora/RHEL:    sudo dnf install tesseract-devel leptonica-devel"
    echo "  Arch:           sudo pacman -S tesseract leptonica"
    echo "  openSUSE:       sudo zypper install tesseract-ocr-devel leptonica-devel"
    echo ""
    echo "  Fedora Atomic / immutable (Bazzite, Silverblue, Kinoite, etc.) -"
    echo "  regular dnf is blocked on these, use rpm-ostree and reboot after:"
    echo "      rpm-ostree install tesseract-devel leptonica-devel"
    echo "      (then reboot, then run this script again)"
    echo ""
}

try_install_tesseract_packages() {
    echo "libtesseract/libleptonica were not found on this system - attempting"
    echo "to install them automatically (detected package manager: ${PACKAGE_MANAGER:-none})."
    echo ""

    case "$PACKAGE_MANAGER" in
        apt)
            $SUDO apt-get update && \
            $SUDO apt-get install -y libtesseract-dev libleptonica-dev
            ;;
        dnf)
            $SUDO dnf install -y tesseract-devel leptonica-devel
            ;;
        yum)
            $SUDO yum install -y tesseract-devel leptonica-devel
            ;;
        pacman)
            $SUDO pacman -Sy --noconfirm tesseract leptonica
            ;;
        zypper)
            $SUDO zypper --non-interactive install tesseract-ocr-devel leptonica-devel
            ;;
        apk)
            echo "Alpine detected - installing tesseract-ocr-dev/leptonica-dev, but note"
            echo "this app's .NET publish is glibc-based; running it on musl-based"
            echo "Alpine at all needs a separate 'linux-musl-x64' publish, which is"
            echo "outside what this script handles. This install step alone will not"
            echo "be enough to run the app on Alpine."
            $SUDO apk add --no-cache tesseract-ocr-dev leptonica-dev
            ;;
        rpm-ostree)
            echo "This is a Fedora Atomic / immutable system (ostree-based) - packages"
            echo "cannot be installed live the way a normal dnf install works here."
            echo "Run this manually, then REBOOT, then run this script again:"
            echo ""
            echo "    rpm-ostree install tesseract-devel leptonica-devel"
            echo ""
            return 1
            ;;
        *)
            echo "Could not detect a supported package manager (apt/dnf/yum/pacman/zypper/apk)."
            return 1
            ;;
    esac
}

# ============================================================================
# Finding the libraries (§118)
# ============================================================================
# Leptonica ships under TWO different library names depending on how old the
# distro's copy is, and this script only ever looked for one of them:
#
#   liblept.so.5        leptonica 1.82 and earlier - Ubuntu 24.04 LTS
#                       (package liblept5, file liblept.so.5.0.4)
#   libleptonica.so.6   leptonica 1.83+ - Fedora and other current distros
#
# Upstream renamed the soname; the distros are simply on different sides of
# that change. Searching only for "libleptonica.so" therefore worked on
# Fedora and silently failed on Ubuntu, which is exactly the split that was
# observed: Fedora was the distro this was first tested on, and it passed.
#
# The failure was particularly hard to read from the outside, because the
# install step and the search step disagreed in a way that looked like
# nonsense. apt reported "libleptonica-dev is already the newest version",
# the re-search still found nothing, and the script printed "Missing:
# libleptonica" underneath. Both statements were true: the package WAS
# installed, and the file it installed was not named what we were looking
# for. Anything that reports a missing dependency should say what it looked
# for, which is why the failure path below now prints it.
echo "Looking for system-installed Leptonica and Tesseract libraries..."

# Matches liblept.so.N and libleptonica.so.N alike. Anchored on "lib" and on
# the ".so" so it cannot pick up an unrelated package that merely has these
# letters somewhere in its name.
LEPTONICA_PATTERN="liblept(onica)?\.so"
TESSERACT_PATTERN="libtesseract\.so"

find_lib() {
    ldconfig -p 2>/dev/null | grep -iE "$1" | head -1 | awk '{print $NF}'
}

LEPTONICA_SO=$(find_lib "$LEPTONICA_PATTERN")
TESSERACT_SO=$(find_lib "$TESSERACT_PATTERN")

if [ -z "$LEPTONICA_SO" ] || [ -z "$TESSERACT_SO" ]; then
    if try_install_tesseract_packages; then
        # Refresh the shared-library cache so ldconfig -p picks up what was
        # just installed without needing a new shell/session.
        $SUDO ldconfig 2>/dev/null || true

        LEPTONICA_SO=$(find_lib "$LEPTONICA_PATTERN")
        TESSERACT_SO=$(find_lib "$TESSERACT_PATTERN")
    fi
fi

if [ -z "$LEPTONICA_SO" ] || [ -z "$TESSERACT_SO" ]; then
    echo ""
    echo "Still could not find one or both libraries after attempting an automatic install."
    print_manual_instructions
    [ -z "$LEPTONICA_SO" ] && echo "  Missing: leptonica  (searched for: $LEPTONICA_PATTERN)"
    [ -z "$TESSERACT_SO" ] && echo "  Missing: tesseract  (searched for: $TESSERACT_PATTERN)"

    # §118: say what IS there. Without this, a name we failed to
    # anticipate looks identical to a package that is genuinely not
    # installed - and those need opposite responses from whoever reads
    # this next. This block is the difference between one more round
    # trip and an answer.
    echo ""
    echo "For reference, ldconfig knows about these lept/tesseract libraries:"
    ldconfig -p 2>/dev/null | grep -iE "lept|tesseract" | sed 's/^/    /' || true
    if ! ldconfig -p 2>/dev/null | grep -qiE "lept|tesseract"; then
        echo "    (none at all - so the packages really are not installed)"
    fi
    exit 1
fi

echo "Found leptonica:  $LEPTONICA_SO"
echo "Found tesseract:  $TESSERACT_SO"

# §118: ldconfig's cache can outlive the file it points at - a purged or
# half-upgraded package leaves an entry behind. Creating a symlink to a file
# that is not there would still let this script print "Done", and would fail
# later as the same DllNotFoundException it exists to prevent, which is the
# one outcome worth going out of the way to avoid.
for CANDIDATE_LIB in "$LEPTONICA_SO" "$TESSERACT_SO"; do
    if [ ! -e "$CANDIDATE_LIB" ]; then
        echo ""
        echo "ERROR: ldconfig lists $CANDIDATE_LIB but that file does not exist."
        echo "The library cache is stale. Refresh it and run this again:"
        echo "    sudo ldconfig"
        exit 1
    fi
done

# TesseractOCR's loader (InteropDotNet/LibraryLoader.cs) never checks a
# candidate base directory (the exe's own folder, AppDomain.BaseDirectory,
# or the process's working directory) directly - it always appends a
# platform-named subfolder first ("x64" for every 64-bit OS, Linux and
# macOS included - see SystemManager.GetPlatformName()) and only looks
# inside THAT. A symlink placed directly in the publish folder - what this
# script used to do - is never actually checked. Confirmed straight from
# that source after real Linux VM tests kept failing with the identical
# DllNotFoundException even immediately after this script reported success
# on a fresh app restart. See MIGRATION_GUIDE.md.
mkdir -p "$APP_DIR/x64"
ln -sf "$LEPTONICA_SO" "$APP_DIR/x64/libleptonica-1.85.0.dll.so"
ln -sf "$TESSERACT_SO" "$APP_DIR/x64/libtesseract55.dll.so"

# Also symlinked at the top level - costs nothing, and hedges against any
# other TesseractOCR version that might check there too.
ln -sf "$LEPTONICA_SO" "$APP_DIR/libleptonica-1.85.0.dll.so"
ln -sf "$TESSERACT_SO" "$APP_DIR/libtesseract55.dll.so"

# Separate issue, found one layer deeper once the fix above got past the
# original error: TesseractOCR's Unix loading logic also needs libdl itself
# (to reach dlopen/dlsym/dlerror via P/Invoke, which it then uses to load
# Leptonica/Tesseract manually) - and that specific lookup only ever checks
# the publish folder's TOP LEVEL (never the "x64" subfolder, and unlike a
# plain dlopen() call, never falls through to the OS's own normal library
# search path either). On a distro where glibc merged dlopen/dlsym/dlerror
# into libc.so.6 itself (glibc 2.34+, which covers most current distros)
# and no "-dev" package is installed to provide a standalone libdl.so file,
# this fails with "Unable to load shared library 'libdl'" the moment OCR
# first tries to run - confirmed from a real VM log showing exactly that,
# immediately after the x64 fix above got past the original error. See
# MIGRATION_GUIDE.md.
LIBDL_SO=$(ldconfig -p 2>/dev/null | grep -i "libdl\.so" | head -1 | awk '{print $NF}')
if [ -z "$LIBDL_SO" ]; then
    # No standalone libdl on this system - glibc folded its symbols into
    # libc.so.6 directly, which still exports them under the same names.
    LIBDL_SO=$(ldconfig -p 2>/dev/null | grep -i "^[[:space:]]*libc\.so\.6" | head -1 | awk '{print $NF}')
fi

if [ -n "$LIBDL_SO" ]; then
    ln -sf "$LIBDL_SO" "$APP_DIR/libdl.so"
else
    echo ""
    echo "Warning: could not find libdl or libc on this system via ldconfig."
    echo "Tracking may still fail with \"Unable to load shared library 'libdl'\"."
    echo "If it does, find your system's libc.so.6 by hand (commonly under"
    echo "/lib/x86_64-linux-gnu or /usr/lib64) and symlink it yourself:"
    echo "    ln -sf /path/to/libc.so.6 $APP_DIR/libdl.so"
fi

echo ""
echo "Done - created symlinks in $APP_DIR/x64 (the folder the loader"
echo "actually searches for Leptonica/Tesseract) and, as a harmless"
echo "fallback, in $APP_DIR itself:"
echo "  x64/libleptonica-1.85.0.dll.so -> $LEPTONICA_SO"
echo "  x64/libtesseract55.dll.so -> $TESSERACT_SO"
if [ -n "$LIBDL_SO" ]; then
    echo "  libdl.so -> $LIBDL_SO (top level only - that lookup doesn't check x64/)"
fi
echo ""
echo "You can now try tracking again."
