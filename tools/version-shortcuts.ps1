#requires -Version 5.1
<#
  §245. Drops a "Set Version" shortcut into every folder of the project, so
  whichever folder you happen to have open reminds you where the version
  lives and lets you change it on the spot.

      .\tools\version-shortcuts.ps1            create or refresh them
      .\tools\version-shortcuts.ps1 -Remove    take them all away again

  Each shortcut runs tools\set-version.ps1, which shows the current version
  and asks for a new one. It carries the app icon, so it is recognisable in a
  folder full of source files.

  Folders that hold build output or tool state are skipped - bin, obj, .vs,
  .git, publish, node_modules - because a shortcut there would be deleted by
  the next clean, or would be one more thing in a folder nobody browses.

  The shortcuts are NOT committed: .gitignore has *.lnk. They are a local
  convenience on this machine, not part of the project, and anyone who clones
  the repository can make their own by running this.
#>

[CmdletBinding()]
param([switch]$Remove)

$ErrorActionPreference = 'Stop'

$repo     = Split-Path -Parent $PSScriptRoot
$target   = Join-Path $repo 'tools\set-version.ps1'
$icon     = Join-Path $repo 'ProTracker_Icon.ico'
$linkName = 'Set Version.lnk'

$skip = @('bin', 'obj', '.vs', '.git', 'publish', 'node_modules', 'tessdata', 'TestResults')

if (-not (Test-Path -LiteralPath $target)) {
  Write-Host ''
  Write-Host "Could not find $target" -ForegroundColor Red
  Write-Host ''
  exit 1
}

# The repository root plus every folder under it, minus the skipped ones and
# anything inside them.
$folders = @($repo) + (
  Get-ChildItem -LiteralPath $repo -Directory -Recurse -Force |
    Where-Object {
      $relative = $_.FullName.Substring($repo.Length).Trim('\')
      $parts = $relative -split '\\'
      -not ($parts | Where-Object { $skip -contains $_ })
    } |
    ForEach-Object { $_.FullName }
)

if ($Remove) {
  $gone = 0
  foreach ($folder in $folders) {
    $path = Join-Path $folder $linkName
    if (Test-Path -LiteralPath $path) { Remove-Item -LiteralPath $path -Force; $gone++ }
  }
  Write-Host ''
  Write-Host "Removed $gone shortcut(s)." -ForegroundColor Green
  Write-Host ''
  exit 0
}

$shell = New-Object -ComObject WScript.Shell
$made = 0
try {
  foreach ($folder in $folders) {
    $path = Join-Path $folder $linkName
    $link = $shell.CreateShortcut($path)
    $link.TargetPath       = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
    $link.Arguments        = "-NoProfile -ExecutionPolicy Bypass -NoExit -File `"$target`""
    $link.WorkingDirectory = $repo
    $link.Description      = 'Show and set the ProTracker version (csproj <Version>)'
    if (Test-Path -LiteralPath $icon) { $link.IconLocation = $icon }
    $link.Save()
    $made++
  }
}
finally {
  [void][Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
}

Write-Host ''
Write-Host "Created $made '$linkName' shortcut(s), one per folder." -ForegroundColor Green
Write-Host 'They run tools\set-version.ps1 and are ignored by git.' -ForegroundColor Green
Write-Host 'Remove them any time with:  .\tools\version-shortcuts.ps1 -Remove' -ForegroundColor Cyan
Write-Host ''
