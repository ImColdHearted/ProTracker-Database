#!/usr/bin/env python3
"""
§424. Zips the published builds for release, with the Unix permission bits
the Linux and macOS builds need INSIDE the zip.

THE PROBLEM. A zip made on Windows - Explorer's "Send to compressed folder",
7-Zip, PowerShell's Compress-Archive, .NET's ZipFile - carries no Unix
permissions at all. Extract it on Linux and every file comes out 644: the
app binary is not executable, linux-start.sh is not executable, and the
user is in a file manager ticking "Allow executing file as program" before
anything runs (the in-app updater knows this and chmods after it extracts;
a person unzipping a download by hand gets no such help). Four reports of
the same thing from the same tester.

THE FIX. The zip format has a place for Unix modes - the upper 16 bits of
each entry's "external attributes" - and extractors honour it when the
entry says it was made on Unix. Windows tools never write it; this script
does, for every entry, whatever machine it runs on: 755 for the app
binary, every *.sh, every shared library and createdump, 644 for the rest,
755 for folders. `unzip`, GNOME's and KDE's archive tools, macOS's Archive
Utility and `ditto` all read it, so the extracted folder works at once.

WHAT IT MAKES. One zip per runtime folder under publish/, named as
Backend/version.json and the forum post name them:

    publish/linux-x64    -> ProTrackerDatabase-LinuxX64.zip
    publish/linux-arm64  -> ProTrackerDatabase-LinuxArm64.zip
    publish/osx-x64      -> ProTrackerDatabase-osx-x64.zip
    publish/osx-arm64    -> ProTrackerDatabase-osx-arm64.zip
    publish/win-x64      -> ProTrackerDatabase-win-x64.zip

Each zip wraps its files in one folder of the zip's own name, which is
what the forum instructions assume ("drag the unzipped folder into the
Terminal") and what the updater's §370 search accepts. Every file under the
runtime folder goes in - nothing is filtered, because the publish folder
is the build.

It also prints the sha256 and byte count of each zip in the shape
version.json's "downloads" block wants, and with --update-version-json
writes them into Backend/version.json for the runtimes it packaged (the
url is left as it is; only sha256 and bytes are filled).

USAGE, from the repo root, after publishing:

    py tools\\package-release.py
    py tools\\package-release.py --only linux-x64 linux-arm64
    py tools\\package-release.py --update-version-json

Python 3.8+, standard library only. No Python? `winget install
Python.Python.3.12` once, or run it from WSL - the bits are the same.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import sys
import time
import zipfile
from pathlib import Path

APP_NAME = "ProTrackerDatabase.Avalonia"

# Runtime folder -> zip name (without .zip). The names are the ones already
# on the forum and in version.json; changing one here changes a download URL.
ZIP_NAMES = {
    "linux-x64": "ProTrackerDatabase-LinuxX64",
    "linux-arm64": "ProTrackerDatabase-LinuxArm64",
    "osx-x64": "ProTrackerDatabase-osx-x64",
    "osx-arm64": "ProTrackerDatabase-osx-arm64",
    "win-x64": "ProTrackerDatabase-win-x64",
}

# version.json keys its downloads by runtime, and two of them are spelt
# differently from the folder names.
VERSION_JSON_KEYS = {
    "linux-x64": "linux-x64",
    "linux-arm64": "linux-arm64",
    "osx-x64": "osx-x64",
    "osx-arm64": "osx-arm64",
    "win-x64": "win-x64",
}

EXECUTABLE_SUFFIXES = (".sh", ".command", ".so", ".dylib")
EXECUTABLE_NAMES = {APP_NAME, "createdump"}

# "Version made by" host: 3 is Unix. This is the byte extractors check
# before they trust the mode bits; Python sets 0 (FAT) on Windows unless
# told otherwise, and 0 means "no permissions here".
MADE_ON_UNIX = 3

DIR_MODE = 0o755
EXEC_MODE = 0o755
FILE_MODE = 0o644

MSDOS_DIR_FLAG = 0x10


def mode_for(relative: Path) -> int:
    name = relative.name

    if name in EXECUTABLE_NAMES:
        return EXEC_MODE

    if name.lower().endswith(EXECUTABLE_SUFFIXES):
        return EXEC_MODE

    # A shared library versioned after its suffix: libfoo.so.1
    if ".so." in name.lower():
        return EXEC_MODE

    return FILE_MODE


def zip_time(path: Path) -> tuple:
    stamp = time.localtime(path.stat().st_mtime)
    # Zip timestamps start at 1980.
    year = max(stamp.tm_year, 1980)
    return (year, stamp.tm_mon, stamp.tm_mday, stamp.tm_hour, stamp.tm_min, stamp.tm_sec)


def add_directory(zf: zipfile.ZipFile, arcname: str, source: Path) -> None:
    info = zipfile.ZipInfo(arcname.rstrip("/") + "/", date_time=zip_time(source))
    info.create_system = MADE_ON_UNIX
    info.external_attr = (0o040000 | DIR_MODE) << 16 | MSDOS_DIR_FLAG
    info.compress_type = zipfile.ZIP_STORED
    zf.writestr(info, b"")


def add_file(zf: zipfile.ZipFile, arcname: str, source: Path, mode: int) -> None:
    info = zipfile.ZipInfo(arcname, date_time=zip_time(source))
    info.create_system = MADE_ON_UNIX
    info.external_attr = (0o100000 | mode) << 16
    info.compress_type = zipfile.ZIP_DEFLATED

    with source.open("rb") as handle, zf.open(info, "w", force_zip64=True) as target:
        while True:
            chunk = handle.read(1024 * 1024)
            if not chunk:
                break
            target.write(chunk)


def package(runtime_dir: Path, out_dir: Path) -> Path:
    runtime = runtime_dir.name
    zip_name = ZIP_NAMES.get(runtime, f"ProTrackerDatabase-{runtime}")
    zip_path = out_dir / f"{zip_name}.zip"

    exe = runtime_dir / (APP_NAME + (".exe" if runtime.startswith("win") else ""))

    if not exe.exists():
        raise SystemExit(f"{runtime_dir}: no {exe.name} here - is this a published build?")

    files = 0
    executables = 0

    with zipfile.ZipFile(zip_path, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as zf:
        add_directory(zf, zip_name, runtime_dir)

        for root, dirs, names in os.walk(runtime_dir):
            root_path = Path(root)
            dirs.sort()
            names.sort()

            for d in dirs:
                rel = (root_path / d).relative_to(runtime_dir)
                add_directory(zf, f"{zip_name}/{rel.as_posix()}", root_path / d)

            for n in names:
                source = root_path / n
                rel = source.relative_to(runtime_dir)
                mode = mode_for(rel)
                add_file(zf, f"{zip_name}/{rel.as_posix()}", source, mode)
                files += 1
                if mode == EXEC_MODE:
                    executables += 1

    print(f"  {zip_path.name}: {files} files, {executables} marked executable")
    return zip_path


def sha256_of(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as handle:
        for chunk in iter(lambda: handle.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def main() -> int:
    parser = argparse.ArgumentParser(description="Zip the published builds with Unix permission bits.")
    parser.add_argument("--publish", default="publish", help="the folder holding one subfolder per runtime (default: publish)")
    parser.add_argument("--out", default=None, help="where the zips go (default: beside the publish folder)")
    parser.add_argument("--only", nargs="*", default=None, help="runtime folders to package (default: every one present)")
    parser.add_argument("--update-version-json", action="store_true",
                        help="write each zip's sha256 and bytes into Backend/version.json")
    args = parser.parse_args()

    repo = Path(__file__).resolve().parent.parent
    publish = (repo / args.publish) if not Path(args.publish).is_absolute() else Path(args.publish)
    out_dir = Path(args.out) if args.out else publish.parent

    if not publish.is_dir():
        print(f"No publish folder at {publish}. Publish first, or pass --publish.", file=sys.stderr)
        return 1

    runtimes = [p for p in sorted(publish.iterdir()) if p.is_dir()]

    if args.only:
        wanted = set(args.only)
        runtimes = [p for p in runtimes if p.name in wanted]
        missing = wanted - {p.name for p in runtimes}
        if missing:
            print(f"Not under {publish}: {', '.join(sorted(missing))}", file=sys.stderr)
            return 1

    if not runtimes:
        print(f"Nothing to package under {publish}.", file=sys.stderr)
        return 1

    print(f"Packaging from {publish} into {out_dir}:")

    results = {}

    for runtime_dir in runtimes:
        zip_path = package(runtime_dir, out_dir)
        results[runtime_dir.name] = {
            "path": zip_path,
            "sha256": sha256_of(zip_path),
            "bytes": zip_path.stat().st_size,
        }

    print()
    print("For Backend/version.json:")

    for runtime, r in results.items():
        key = VERSION_JSON_KEYS.get(runtime, runtime)
        print(f'  "{key}": {{ "sha256": "{r["sha256"]}", "bytes": {r["bytes"]} }}')

    if args.update_version_json:
        version_json = repo / "Backend" / "version.json"

        if not version_json.exists():
            print(f"\n{version_json} is not there; nothing written.", file=sys.stderr)
            return 1

        raw = version_json.read_text(encoding="utf-8")
        data = json.loads(raw)
        downloads = data.setdefault("downloads", {})
        touched = []

        for runtime, r in results.items():
            key = VERSION_JSON_KEYS.get(runtime, runtime)
            entry = downloads.get(key)

            if entry is None:
                print(f"  version.json has no \"{key}\" download; left alone - add its url first.")
                continue

            entry["sha256"] = r["sha256"]
            entry["bytes"] = r["bytes"]
            touched.append(key)

        if touched:
            newline = "\r\n" if "\r\n" in raw else "\n"
            text = json.dumps(data, indent=2, ensure_ascii=False).replace("\n", newline)
            if raw.endswith(newline):
                text += newline
            version_json.write_text(text, encoding="utf-8")
            print(f"\nWrote sha256 and bytes for {', '.join(touched)} into {version_json.relative_to(repo)}.")

    return 0


if __name__ == "__main__":
    sys.exit(main())
