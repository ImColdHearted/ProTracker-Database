#requires -Version 5.1
<#
  §253. Deletes the source files of the guild Events board, which nothing in
  the client references any more. Run it once from anywhere; it finds the
  repository from its own location.

  What it removes: the Events window and its board, Create Event, Remove
  Event, the entry submission windows and their view models, the guild event
  services and models, the event-submitter name service, the §137 leftovers
  (EventSettingsService and its model) and the one converter only Create
  Event used. The list is fixed - it is exactly what MIGRATION_GUIDE.md §253
  names - and every path is checked to be inside the repository before
  anything is touched.

  What it does not remove: the Worker (Backend/EventsWorker) - the routes
  stay by choice - and, unless -AlsoLocalData is given, the board's files in
  this machine's data folder (posted events, entries and their screenshots,
  the remembered submitter name). Those are harmless and yours; the switch is
  there for when you want them gone too.

  -DryRun lists what would go and deletes nothing.
#>
[CmdletBinding()]
param(
    [switch] $DryRun,
    [switch] $AlsoLocalData
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$csproj = Join-Path $root 'ProTrackerDatabase.Avalonia.csproj'

if (-not (Test-Path -LiteralPath $csproj)) {
    throw "This script expects to live in the repository's tools folder; $csproj was not found."
}

# The shared files were edited in the same section as this script. If the
# menu still opens the board, the edits are not in place yet, and deleting
# the files first would break the build in a confusing order.
$mainWindow = Get-Content -LiteralPath (Join-Path $root 'Views\MainWindow.axaml') -Raw
if ($mainWindow -match 'EventsButton_Click') {
    throw 'Views\MainWindow.axaml still opens the Events board - apply the section 253 edits to the shared files first, then run this.'
}

$files = @(
    'Views\EventsWindow.axaml',
    'Views\EventsWindow.axaml.cs',
    'Views\CreateEventWindow.axaml',
    'Views\CreateEventWindow.axaml.cs',
    'Views\RemoveEventWindow.axaml',
    'Views\RemoveEventWindow.axaml.cs',
    'Views\EventEntriesWindow.axaml',
    'Views\EventEntriesWindow.axaml.cs',
    'Views\EventEntryDetailWindow.axaml',
    'Views\EventEntryDetailWindow.axaml.cs',
    'Views\EventPokemonPickerWindow.axaml',
    'Views\EventPokemonPickerWindow.axaml.cs',
    'Views\SubmitPokemonWindow.axaml',
    'Views\SubmitPokemonWindow.axaml.cs',
    'ViewModels\EventsViewModel.cs',
    'ViewModels\CreateEventViewModel.cs',
    'ViewModels\RemoveEventViewModel.cs',
    'ViewModels\EventEntriesViewModel.cs',
    'ViewModels\EventEntryDetailViewModel.cs',
    'ViewModels\EventPokemonPickerViewModel.cs',
    'ViewModels\SubmitPokemonViewModel.cs',
    'Services\GuildEventService.cs',
    'Services\GuildEventEntryService.cs',
    'Services\EventSubmitterNameService.cs',
    'Services\EventSettingService.cs',
    'Models\GuildEvent.cs',
    'Models\EventEntry.cs',
    'Models\EventSettings.cs',
    'Converters\EnumDisplayNameConverter.cs'
)

$rootFull = [System.IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
$removed = 0
$missing = 0

foreach ($relative in $files) {
    $path = [System.IO.Path]::GetFullPath((Join-Path $root $relative))

    if (-not $path.StartsWith($rootFull, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to touch $path - it is outside the repository."
    }

    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        Write-Host "  already gone  $relative"
        $missing++
        continue
    }

    if ($DryRun) {
        Write-Host "  would delete  $relative"
    } else {
        Remove-Item -LiteralPath $path -Force
        Write-Host "  deleted       $relative"
    }
    $removed++
}

Write-Host ''
if ($DryRun) {
    Write-Host ("{0} file(s) would be deleted, {1} already gone." -f $removed, $missing)
} else {
    Write-Host ("{0} file(s) deleted, {1} already gone." -f $removed, $missing)
}

# A last look for anything left that names a deleted type - a compile error
# in the making. Comments are skipped; the words are the class names.
$names = @('EventsWindow', 'CreateEventWindow', 'RemoveEventWindow', 'EventEntriesWindow', 'EventEntryDetailWindow',
           'EventPokemonPickerWindow', 'SubmitPokemonWindow', 'EventsViewModel', 'CreateEventViewModel', 'RemoveEventViewModel',
           'EventEntriesViewModel', 'EventEntryDetailViewModel', 'EventPokemonPickerViewModel', 'SubmitPokemonViewModel',
           'GuildEventService', 'GuildEventEntryService', 'EventSubmitterNameService', 'EventSettingsService',
           'GuildEventType', 'GuildEventCardItem', 'EnumDisplayNameConverter')
$pattern = '\b(' + (($names | ForEach-Object { [regex]::Escape($_) }) -join '|') + ')\b'
$leftovers = @()

Get-ChildItem -LiteralPath $root -Recurse -Include *.cs, *.axaml -File |
    Where-Object { $_.FullName -notmatch '\\(bin|obj|publish|PokemonSim[^\\]*|Backend)\\' } |
    ForEach-Object {
        $lineNumber = 0
        $inXmlComment = $false
        foreach ($line in [System.IO.File]::ReadLines($_.FullName)) {
            $lineNumber++

            # XAML comments span lines; C# doc comments and line comments do
            # not. Either way the words inside them are history, not code.
            $code = $line
            if ($inXmlComment) {
                if ($code -match '-->') { $code = $code -replace '^.*?-->', ''; $inXmlComment = $false } else { continue }
            }
            while ($code -match '<!--') {
                if ($code -match '<!--.*?-->') { $code = $code -replace '<!--.*?-->', '' }
                else { $code = $code -replace '<!--.*$', ''; $inXmlComment = $true }
            }
            $code = $code -replace '//.*$', ''

            if ($code -match $pattern) {
                $leftovers += ('{0}:{1}: {2}' -f $_.FullName.Substring($rootFull.Length), $lineNumber, $line.Trim())
            }
        }
    }

if ($leftovers.Count -gt 0) {
    Write-Host ''
    Write-Host 'Still named in code (each of these is a build error until it goes):'
    $leftovers | ForEach-Object { Write-Host "  $_" }
} else {
    Write-Host 'Nothing left in code names a deleted type.'
}

if ($AlsoLocalData) {
    $database = Join-Path $env:LOCALAPPDATA 'ProTracker\Database'
    $dataPaths = @(
        (Join-Path $database 'guild-events.json'),
        (Join-Path $database 'guild-event-entries.json'),
        (Join-Path $database 'EventEntries'),
        (Join-Path $database 'event-submitter-name.txt'),
        (Join-Path $env:LOCALAPPDATA 'PRO Tracker & Database\event.json')
    )

    Write-Host ''
    foreach ($dataPath in $dataPaths) {
        if (-not (Test-Path -LiteralPath $dataPath)) {
            Write-Host "  not present   $dataPath"
            continue
        }
        if ($DryRun) {
            Write-Host "  would delete  $dataPath"
        } else {
            Remove-Item -LiteralPath $dataPath -Recurse -Force
            Write-Host "  deleted       $dataPath"
        }
    }
}

Write-Host ''
Write-Host 'Next: dotnet build, then commit with tools\git-save.ps1.'
