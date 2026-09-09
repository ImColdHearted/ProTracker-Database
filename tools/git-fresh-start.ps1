#requires -Version 5.1
<#
  §247, rewritten by §248. Rebuilds this repository's LOCAL history as a single
  commit containing only what .gitignore now allows.

  Why this exists. Deleting a file in a new commit does not remove it from
  GitHub - the older commit still holds it, and anyone can browse to that
  commit and read it. MIGRATION_GUIDE.md went up in the first push and was
  removed afterwards, so it stayed reachable, and so did everything it says
  about the admin gate and the Worker's routes. The only reliable answer is
  that no commit containing it is ever pushed again, which means a history
  with no such commit in it.

  HOW IT DOES IT, and why this is the second attempt.

  The first version created an orphan branch, committed on it, deleted the old
  branch and renamed. That has a failure mode it handled badly: if any step
  after the orphan checkout failed, the run left a branch called
  "fresh-start-tmp" behind AND left HEAD standing on it. The next run tried to
  tidy up with "git branch -D fresh-start-tmp", which cannot delete the branch
  you are currently on, and then died on "a branch named 'fresh-start-tmp'
  already exists". It also threw that cleanup's result away, so the failure was
  silent.

  There is nothing to preserve here - the whole point is to discard history -
  so the clever version was solving a problem that does not exist. This one
  removes the .git folder outright and initialises a new repository over the
  same working tree. One commit, a brand new object store with nothing
  unreachable hiding in it, no temporary branch, and no state a half-finished
  run can leave behind: either .git exists and is the new one, or it does not
  exist and the next run starts over cleanly.

  ORDER MATTERS:

    1. Run this.
    2. Delete the repository on GitHub - Settings, then Delete this repository.
       Recreate it, empty, under the same name so the remote URL still works.
    3. .\tools\git-save.ps1 -Message "initial commit"

  Doing 3 before 2 pushes the clean history alongside the old commits rather
  than instead of them. If GitHub has anything at all on the branch - even the
  README it offers to create for you - git-save.ps1 -Force will replace it,
  and since §244 it fetches first so the safety lease has something to hold.

  This DISCARDS all local git history, branches and stashes, and asks before
  doing anything. It does not touch your files: everything on disk stays where
  it is, including MIGRATION_GUIDE.md, which simply stops being tracked.
#>

[CmdletBinding()]
param(
  [string]$Message = 'initial commit',
  [string]$Branch = 'main',
  [string]$Remote
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

function Fail($text) { Write-Host ''; Write-Host $text -ForegroundColor Red; Write-Host ''; exit 1 }
function Say($text)  { Write-Host $text -ForegroundColor Cyan }

# §243's helper: git writes ordinary output to stderr, and under
# $ErrorActionPreference = 'Stop' PowerShell turns that into a terminating
# error. Every git call goes through this, and every one is judged by its exit
# code rather than by whether it printed something.
function Invoke-Git {
  [CmdletBinding()]
  param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GitArgs)
  $previous = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try { $raw = & git @GitArgs 2>&1; $code = $LASTEXITCODE }
  finally { $ErrorActionPreference = $previous }
  [pscustomobject]@{
    Code = $code
    Text = (($raw | ForEach-Object { $_.ToString() }) -join "`n").Trim()
  }
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
  Fail 'git is not installed, or is not on PATH. Install it from https://git-scm.com/download/win and reopen this window.'
}

Set-Location -LiteralPath $repo

$dotGit = Join-Path $repo '.git'
$hadRepo = Test-Path -LiteralPath $dotGit

# ------------------------------------------------------------- what is here
$countBefore = 0
$existingRemote = ''

if ($hadRepo) {
  # §242's guard, and it matters more here than anywhere: there is a stray
  # .git in the Windows user profile folder, and deleting the wrong .git would
  # be an unrecoverable mistake rather than an inconvenient one. Only proceed
  # when git agrees the repository root is this exact folder.
  $top = Invoke-Git rev-parse --show-toplevel
  if ($top.Code -ne 0 -or -not $top.Text) { Fail "git could not identify a repository here:`n$($top.Text)" }

  $topFull  = [IO.Path]::GetFullPath(($top.Text -replace '/', '\')).TrimEnd('\')
  $repoFull = [IO.Path]::GetFullPath($repo).TrimEnd('\')

  if ($topFull -ne $repoFull) {
    Fail @"
REFUSING TO RUN.

git says the repository root is:
  $topFull
but this script lives in:
  $repoFull

This folder is inside someone else's repository - most likely the stray .git
in your user profile folder. Removing a .git from here would remove theirs.
"@
  }

  $c = Invoke-Git rev-list --count HEAD
  if ($c.Code -eq 0 -and $c.Text) { $countBefore = [int]$c.Text }

  $r = Invoke-Git remote get-url origin
  if ($r.Code -eq 0 -and $r.Text) { $existingRemote = $r.Text }
}

if (-not $Remote) { $Remote = $existingRemote }

# ------------------------------------------------------------- say it plainly
Write-Host ''
Write-Host 'REBUILD LOCAL HISTORY' -ForegroundColor Yellow
if ($hadRepo) {
  Write-Host "  The .git folder is REMOVED and rebuilt. $countBefore commit(s), every" -ForegroundColor Yellow
  Write-Host '  branch and every stash go with it.' -ForegroundColor Yellow
} else {
  Write-Host '  There is no repository here yet, so one is created.' -ForegroundColor Yellow
}
Write-Host '  Your files are NOT touched. Nothing is pushed.' -ForegroundColor Yellow
if ($Remote) { Write-Host "  origin is put back as: $Remote" -ForegroundColor Yellow }
else { Write-Host '  No origin is set - git-save.ps1 will ask for the URL.' -ForegroundColor Yellow }

$watch = @('MIGRATION_GUIDE.md', 'sync-back-sprites.ps1', 'settings.VisualStudio.json')

if ($hadRepo) {
  Write-Host ''
  Write-Host '  Tracked today, and will stop being tracked:' -ForegroundColor Yellow
  $willDrop = @()
  foreach ($path in $watch) {
    $t = Invoke-Git ls-files -- $path
    if ($t.Code -eq 0 -and $t.Text) { $willDrop += $path; Write-Host "    $path" }
  }
  $dir = Invoke-Git ls-files -- 'Claude outputs'
  if ($dir.Code -eq 0 -and $dir.Text) {
    $willDrop += 'Claude outputs/'
    Write-Host ("    Claude outputs/  ({0} file(s))" -f ($dir.Text -split "`n").Count)
  }
  if ($willDrop.Count -eq 0) { Write-Host '    (none - .gitignore already covers everything)' }
}

Write-Host ''
$answer = Read-Host 'Type  rebuild  to confirm'
if ($answer -cne 'rebuild') { Fail 'Not confirmed. Nothing was changed.' }

# ------------------------------------------------------------------- rebuild
if ($hadRepo) {
  # Git marks objects read-only, so a plain delete refuses partway and leaves
  # half a .git behind - which is exactly the half-finished state this rewrite
  # exists to avoid. -Force clears the attribute as it goes.
  try {
    Remove-Item -LiteralPath $dotGit -Recurse -Force
  }
  catch {
    Fail @"
Could not remove the .git folder:
$($_.Exception.Message)

Something is holding it open. Close Visual Studio and any git client, then run
this again. Your files are untouched and the repository is still intact.
"@
  }

  if (Test-Path -LiteralPath $dotGit) {
    Fail 'The .git folder is still there after removing it. Stopping rather than guessing.'
  }
  Say 'Old history removed.'
}

$init = Invoke-Git init --initial-branch=$Branch
if ($init.Code -ne 0) {
  # --initial-branch needs git 2.28. Older git: init, then rename.
  $init = Invoke-Git init
  if ($init.Code -ne 0) { Fail "git init failed:`n$($init.Text)" }
  $null = Invoke-Git branch -M $Branch
}

# Now that .git is ours, the root guard is a formality - but a formality that
# costs nothing and would catch a genuinely strange filesystem.
$top = Invoke-Git rev-parse --show-toplevel
$topFull  = [IO.Path]::GetFullPath((($top.Text) -replace '/', '\')).TrimEnd('\')
$repoFull = [IO.Path]::GetFullPath($repo).TrimEnd('\')
if ($top.Code -ne 0 -or $topFull -ne $repoFull) {
  Fail "After init, git says the repository root is '$($top.Text)', not '$repoFull'."
}

if ($Remote) {
  $add = Invoke-Git remote add origin $Remote
  if ($add.Code -ne 0) { Fail "Could not set the remote:`n$($add.Text)" }
}

$add = Invoke-Git add -A
if ($add.Code -ne 0) { Fail "git add failed:`n$($add.Text)" }

$commit = Invoke-Git commit -m $Message
if ($commit.Code -ne 0) { Fail "git commit failed:`n$($commit.Text)" }

# ---------------------------------------------------------------- prove it
$after = Invoke-Git rev-list --count HEAD
if ($after.Code -ne 0 -or [int]$after.Text -ne 1) {
  Fail "Expected exactly 1 commit afterwards, got '$($after.Text)'. Check by hand."
}

$branches = Invoke-Git branch --list
if ($branches.Code -ne 0 -or ($branches.Text -split "`n").Count -ne 1) {
  Fail "Expected exactly one branch afterwards, got:`n$($branches.Text)"
}

$still = @()
foreach ($path in $watch) {
  $t = Invoke-Git ls-files -- $path
  if ($t.Code -eq 0 -and $t.Text) { $still += $path }
}
$d = Invoke-Git ls-files -- 'Claude outputs'
if ($d.Code -eq 0 -and $d.Text) { $still += 'Claude outputs/' }
if ($still.Count -gt 0) { Fail "Still tracked after the rebuild: $($still -join ', '). Check .gitignore." }

# Ignoring a file is not deleting it, and a rebuild that quietly removed the
# engineering log would be a far worse outcome than the one being fixed.
if (-not (Test-Path -LiteralPath (Join-Path $repo 'MIGRATION_GUIDE.md'))) {
  Fail 'MIGRATION_GUIDE.md is no longer on disk. That should not happen - restore it before doing anything else.'
}

$branchNow = Invoke-Git rev-parse --abbrev-ref HEAD
$files = Invoke-Git ls-files

Write-Host ''
Write-Host 'Done. Local history is a single commit in a new repository.' -ForegroundColor Green
Write-Host ("  branch : {0}" -f $branchNow.Text)
Write-Host ("  files  : {0} tracked" -f ($files.Text -split "`n").Count)
Write-Host ("  origin : {0}" -f $(if ($Remote) { $Remote } else { '(not set)' }))
Write-Host '  MIGRATION_GUIDE.md is still on disk and is no longer tracked.' -ForegroundColor Green
Write-Host ''
Write-Host 'NEXT, and in this order:' -ForegroundColor Cyan
Write-Host '  1. On GitHub: Settings -> Delete this repository. Then recreate it,'
Write-Host '     empty, under the same name so the remote URL still works.'
Write-Host '  2. .\tools\git-save.ps1 -Message "initial commit"'
Write-Host '     If GitHub already has anything on the branch, including a README it'
Write-Host '     created for you, add -Force. It fetches first and shows you what it'
Write-Host '     is about to replace.'
Write-Host ''
Write-Host 'Pushing before deleting adds this history beside the old commits' -ForegroundColor Yellow
Write-Host 'instead of replacing them, and the old ones are the problem.' -ForegroundColor Yellow
Write-Host ''
