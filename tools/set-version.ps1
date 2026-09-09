#requires -Version 5.1
<#
  §245. Shows the version this project reports, and sets a new one.

  There is exactly one place the version lives:

      ProTrackerDatabase.Avalonia.csproj    <Version>...</Version>

  and §231 put it there for a reason - it drives AssemblyVersion, FileVersion
  and InformationalVersion together, and Services/AppVersion.cs reads it back
  at runtime. Bug reports and the presence heartbeat both carry it. Setting it
  anywhere else, or in more than one place, is how they stop agreeing.

  Run it with no arguments to see the current version and be asked for a new
  one. Press Enter at the prompt to leave it alone.

      .\tools\set-version.ps1
      .\tools\set-version.ps1 -Version 1.0.2

  The csproj is written back byte for byte apart from the number: same UTF-8
  BOM, same CRLF line endings, same lack of a trailing newline. A version bump
  should not show up in git as a whole-file rewrite.
#>

[CmdletBinding()]
param([string]$Version)

$ErrorActionPreference = 'Stop'

function Fail($text) {
  Write-Host ''
  Write-Host $text -ForegroundColor Red
  Write-Host 'Nothing was changed.' -ForegroundColor Red
  Write-Host ''
  exit 1
}

$repo    = Split-Path -Parent $PSScriptRoot
$csproj  = Join-Path $repo 'ProTrackerDatabase.Avalonia.csproj'

if (-not (Test-Path -LiteralPath $csproj)) { Fail "Could not find $csproj" }

# Read as raw bytes so nothing about the file's shape is guessed at.
$bytes = [IO.File]::ReadAllBytes($csproj)
$hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
$text = [Text.Encoding]::UTF8.GetString($bytes)
if ($hasBom) { $text = $text.Substring(1) }

# NOT $matches - that is a PowerShell automatic variable, filled in by every
# -match operator, and this script uses -match a few lines down.
$found = [regex]::Matches($text, '<Version>([^<]*)</Version>')
if ($found.Count -ne 1) {
  Fail "Expected exactly one <Version> element in the csproj, found $($found.Count). Fix that by hand first."
}

$current = $found[0].Groups[1].Value

Write-Host ''
Write-Host 'ProTracker version' -ForegroundColor Cyan
Write-Host "  file    : ProTrackerDatabase.Avalonia.csproj"
Write-Host "  current : $current" -ForegroundColor Green
Write-Host ''

if (-not $Version) {
  $Version = Read-Host "New version (Enter to leave it at $current)"
  if ([string]::IsNullOrWhiteSpace($Version)) {
    Write-Host ''
    Write-Host "Left at $current." -ForegroundColor Cyan
    Write-Host ''
    exit 0
  }
}

$Version = $Version.Trim()

# One to four dot-separated numbers. .NET will not accept anything else as an
# assembly version, and finding that out at build time is a wasted rebuild.
if ($Version -notmatch '^\d+(\.\d+){0,3}$') {
  Fail "'$Version' is not a version .NET will accept. Use numbers and dots, for example 1.0.2"
}

if ($Version -eq $current) {
  Write-Host "Already $current. Nothing to do." -ForegroundColor Cyan
  Write-Host ''
  exit 0
}

# Replace only inside the one element that matched, by position, so nothing
# else in the file can be touched by a broad pattern.
$g = $found[0].Groups[1]
$updated = $text.Substring(0, $g.Index) + $Version + $text.Substring($g.Index + $g.Length)

# WriteAllText with a UTF8Encoding that knows whether to emit the preamble.
# Concatenating a preamble byte array onto the content array would go through
# PowerShell's Object[] and rely on the binder coercing it back to byte[].
[IO.File]::WriteAllText($csproj, $updated, (New-Object Text.UTF8Encoding($hasBom)))

# Read it back and confirm, rather than assuming the write did what was asked.
$check = [regex]::Matches(
  [Text.Encoding]::UTF8.GetString([IO.File]::ReadAllBytes($csproj)).TrimStart([char]0xFEFF),
  '<Version>([^<]*)</Version>')
if ($check.Count -ne 1 -or $check[0].Groups[1].Value -ne $Version) {
  Fail "The file did not read back as $Version. Check it by hand."
}

Write-Host ''
Write-Host "$current  ->  $Version" -ForegroundColor Green
Write-Host ''
Write-Host 'Rebuild for it to take effect. Then:' -ForegroundColor Cyan
Write-Host "  .\tools\git-save.ps1 -Message `"version $Version`""
Write-Host ''
