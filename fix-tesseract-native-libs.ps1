# Works around a confirmed bug in the TesseractOCR package (Sicos1977 fork) -
# its native-library loader on Linux/macOS constructs filenames like
# "libleptonica-1.85.0.dll.so" (both .dll AND .so/.dylib appended) instead of
# the actual correct filename "libleptonica-1.85.0.so". Confirmed via a GitHub
# issue against this exact package (Sicos1977/TesseractOCR#22) reporting the
# identical "Failed to find library '...dll.so'" error, plus the underlying
# InteropDotNet loader source showing it appends the platform extension onto
# an already-Windows-suffixed filename instead of replacing it.
#
# This creates a duplicate copy of every native library under the broken name
# the loader actually searches for, alongside the correctly-named original -
# a real file copy (not a symlink), since this needs to survive being zipped
# up on Windows and unzipped on Linux/macOS, which isn't guaranteed for a
# Windows-created symlink.
#
# The workaround copies go in an "x64" subfolder of the publish folder, not
# the publish folder itself. TesseractOCR's loader (InteropDotNet/
# LibraryLoader.cs) never checks a candidate base directory directly - it
# always appends a platform-named subfolder first ("x64" for every 64-bit
# OS - see SystemManager.GetPlatformName()) and only looks inside THAT. An
# earlier version of this script copied straight into the publish folder,
# which the loader was never actually going to check - confirmed the hard
# way when real Linux VM logs kept showing the identical DllNotFoundException
# even right after linux-fix-tesseract-ocr.sh (the Linux-side counterpart to
# this script) reported success. Still also copies to the top level as a
# harmless fallback. See MIGRATION_GUIDE.md.
#
# USAGE: run this after every `dotnet publish -r linux-x64` /
# `dotnet publish -r osx-arm64` (etc.) - BEFORE zipping up the output folder
# to send to a tester. See MIGRATION_GUIDE.md.

param(
    [Parameter(Mandatory = $true)]
    [string]$PublishFolder
)

if (-not (Test-Path $PublishFolder)) {
    Write-Error "Folder not found: $PublishFolder"
    exit 1
}

$patterns = @(
    @{ Glob = "lib*.so";     OldExt = ".so";     NewExt = ".dll.so" }     # Linux
    @{ Glob = "lib*.dylib";  OldExt = ".dylib";  NewExt = ".dll.dylib" }  # macOS
)

$x64Folder = Join-Path $PublishFolder "x64"
if (-not (Test-Path $x64Folder)) {
    New-Item -ItemType Directory -Path $x64Folder | Out-Null
}

$totalCopied = 0

foreach ($pattern in $patterns) {
    $files = Get-ChildItem -Path $PublishFolder -Filter $pattern.Glob -File -ErrorAction SilentlyContinue

    foreach ($file in $files) {
        # Skip files that already have the broken name pattern, in case this
        # script gets run twice against the same folder.
        if ($file.Name -like "*$($pattern.NewExt)") {
            continue
        }

        $newName = $file.Name.Substring(0, $file.Name.Length - $pattern.OldExt.Length) + $pattern.NewExt

        # Primary copy: the "x64" subfolder is what TesseractOCR's loader
        # actually searches - see the note above.
        $x64Destination = Join-Path $x64Folder $newName
        Copy-Item -Path $file.FullName -Destination $x64Destination -Force
        Write-Host "Created x64\$newName (workaround copy of $($file.Name))"

        # Also kept at the top level as a harmless fallback.
        $topDestination = Join-Path $file.DirectoryName $newName
        Copy-Item -Path $file.FullName -Destination $topDestination -Force

        $totalCopied++
    }
}

if ($totalCopied -eq 0) {
    Write-Host "No lib*.so or lib*.dylib files found in $PublishFolder - nothing to do."
} else {
    Write-Host ""
    Write-Host "Done - created $totalCopied workaround file(s) (in x64\ and, as a"
    Write-Host "fallback, directly in $PublishFolder too)."
}
