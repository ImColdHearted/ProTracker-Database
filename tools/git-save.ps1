#requires -Version 5.1
<#
  §242. Commits everything in the repository and pushes it to one branch.
  §243 fixed it: every git call now goes through a helper. §244 made the
  force path fetch first, so the safety lease has something to hold. See below.

  Replaces dragging files onto the GitHub website. Run it from anywhere:

      .\tools\git-save.ps1 -Message "section 241, strip pings"

  The first run also sets the repository up: git init, the branch, the remote,
  and the first commit. Every run after that is just add, commit, push.

  WHY THE HELPER. git writes a great deal of perfectly normal output to
  stderr - progress, branch tracking notes, "nothing to commit". PowerShell
  with $ErrorActionPreference = 'Stop' turns a native command's stderr into a
  TERMINATING error, so the first version of this script died on
  "git ls-files --error-unmatch", which reports a not-tracked file on stderr
  by design. Invoke-Git runs git with error handling relaxed, captures both
  streams, and returns the exit code for the caller to judge. Nothing here
  calls git any other way.

  Safety, because two things can go very wrong quietly:

  1. There is a stray .git in the Windows user profile folder. Without a repo
     of its own, "git add -A" here would walk up, find THAT one, and stage the
     entire user profile - Documents, Desktop, AppData, browser profiles. So
     this script checks that git's idea of the repository root is this folder
     and refuses to touch anything if it is not.

  2. This repository is public. Before every push it checks that the admin
     credential has not come back into a tracked file. See §242.

  Nothing is ever force-pushed unless -Force is passed explicitly.
#>

[CmdletBinding()]
param(
  [string]$Message,
  [string]$Remote,
  [string]$Branch = 'main',
  [switch]$Force
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

function Fail($text) { Write-Host ''; Write-Host $text -ForegroundColor Red; Write-Host ''; exit 1 }
function Say($text)  { Write-Host $text -ForegroundColor Cyan }

# The only way this script is allowed to call git. Returns the exit code and
# the combined output; never throws, whatever git writes to stderr.
function Invoke-Git {
  [CmdletBinding()]
  param([Parameter(ValueFromRemainingArguments = $true)][string[]]$GitArgs)

  $previous = $ErrorActionPreference
  $ErrorActionPreference = 'Continue'
  try {
    $raw = & git @GitArgs 2>&1
    $code = $LASTEXITCODE
  }
  finally {
    $ErrorActionPreference = $previous
  }

  [pscustomobject]@{
    Code = $code
    Text = (($raw | ForEach-Object { $_.ToString() }) -join "`n").Trim()
  }
}

if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
  Fail "git is not installed, or is not on PATH. Install it from https://git-scm.com/download/win and reopen this window."
}

Set-Location -LiteralPath $repo

# ---------------------------------------------------------------- first run
if (-not (Test-Path -LiteralPath (Join-Path $repo '.git'))) {
  Say "No repository here yet. Setting one up in $repo"

  $init = Invoke-Git init --initial-branch=$Branch
  if ($init.Code -ne 0) {
    # --initial-branch needs git 2.28. Older git: init, then rename.
    $init = Invoke-Git init
    if ($init.Code -ne 0) { Fail "git init failed:`n$($init.Text)" }
    $null = Invoke-Git branch -M $Branch
  }
  Say "Repository created."
}

# The remote may be missing even when .git exists - an earlier run of this
# script could have stopped between the two.
$originUrl = (Invoke-Git remote get-url origin)
if ($originUrl.Code -ne 0) {
  if (-not $Remote) {
    Write-Host ''
    Write-Host 'Paste the GitHub repository URL, for example:'
    Write-Host '  https://github.com/<you>/<repo>.git'
    $Remote = Read-Host 'Remote URL'
  }
  if ([string]::IsNullOrWhiteSpace($Remote)) { Fail 'No remote URL given. Nothing was changed.' }

  $add = Invoke-Git remote add origin $Remote
  if ($add.Code -ne 0) { Fail "Could not set the remote:`n$($add.Text)" }
  Say "origin set to $Remote"
}
elseif ($Remote -and $Remote -ne $originUrl.Text) {
  $set = Invoke-Git remote set-url origin $Remote
  if ($set.Code -ne 0) { Fail "Could not change the remote:`n$($set.Text)" }
  Say "origin changed to $Remote"
}

# ------------------------------------------------- guard 1: the right repo
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

That means this folder is inside SOMEONE ELSE'S repository - most likely the
stray .git in your user profile folder. Committing from here would stage far
more than this project.

Fix it by creating a repository for this folder first, or by removing the
stray one, then run this again.
"@
}

# --------------------------------- guard 2: no admin credential in the tree
$authFile = Join-Path $repo 'Services\AdminAuthService.cs'
if (Test-Path -LiteralPath $authFile) {
  $auth = Get-Content -LiteralPath $authFile -Raw
  if ($auth -match 'FromBase64String\s*\(\s*"[A-Za-z0-9+/]{16,}={0,2}"') {
    Fail @"
REFUSING TO PUSH.

Services\AdminAuthService.cs contains a base64 literal again. In a public
repository that is the admin salt and hash, readable by anyone, crackable
offline forever.

The credential belongs in %LOCALAPPDATA%\ProTracker\Database\admin-credential.json
- run tools\new-admin-credential.ps1. See MIGRATION_GUIDE.md section 242.
"@
  }
}

# "ls-files -- <path>" prints the path when tracked and nothing when not, and
# exits 0 either way. --error-unmatch would report on stderr instead, which is
# what killed the first version of this script.
$tracked = Invoke-Git ls-files -- 'admin-credential.json'
if ($tracked.Code -eq 0 -and $tracked.Text) {
  Fail 'REFUSING TO PUSH. admin-credential.json is tracked. Run: git rm --cached admin-credential.json'
}

# ------------------------------------------------------------------- commit
$add = Invoke-Git add -A
if ($add.Code -ne 0) { Fail "git add failed:`n$($add.Text)" }

$status = Invoke-Git status --porcelain
if ($status.Code -ne 0) { Fail "git status failed:`n$($status.Text)" }

if (-not $status.Text) {
  Say 'Nothing has changed since the last save.'
}
else {
  if (-not $Message) { $Message = "Save $(Get-Date -Format 'yyyy-MM-dd HH:mm')" }
  $commit = Invoke-Git commit -m $Message
  if ($commit.Code -ne 0) { Fail "git commit failed:`n$($commit.Text)" }
  Say "Committed: $Message"
}

# --------------------------------------------------------------------- push
$upstream = Invoke-Git rev-parse --abbrev-ref --symbolic-full-name '@{u}'

if ($Force) {
  # §244. --force-with-lease needs a lease, and a lease is the remote-tracking
  # ref refs/remotes/origin/<branch>. A repository that was init'd here and
  # never fetched does not have one, so git refuses with "stale info" - which
  # reads like the remote moved, when in fact nothing was ever known about it.
  # Fetching first creates the ref, and the lease then means what it should:
  # replace the remote as it was a moment ago, and stop if someone pushed in
  # between.
  Say 'Fetching the remote so the safety check has something to compare against...'
  $fetch = Invoke-Git fetch origin
  if ($fetch.Code -ne 0) { Fail "Could not reach the remote:`n$($fetch.Text)" }

  $remoteRef = "origin/$Branch"
  $exists = Invoke-Git rev-parse --verify --quiet "$remoteRef^{commit}"

  Write-Host ''
  Write-Host 'FORCE PUSH: whatever is on GitHub for this branch will be REPLACED' -ForegroundColor Yellow
  Write-Host 'by what is on this machine. Anything only on GitHub is lost.' -ForegroundColor Yellow

  if ($exists.Code -eq 0 -and $exists.Text) {
    $count  = Invoke-Git rev-list --count $remoteRef
    $recent = Invoke-Git log --oneline -5 $remoteRef
    Write-Host ''
    Write-Host "About to replace $($count.Text) commit(s) on $remoteRef. The most recent:" -ForegroundColor Yellow
    Write-Host $recent.Text

    # The script can read the remote's copy now that it has been fetched. If
    # the old credential is up there, a force push hides it but does not
    # guarantee it is gone - GitHub keeps unreferenced commits reachable by
    # hash for a while. Rotating is what actually retires it.
    $remoteAuth = Invoke-Git show "${remoteRef}:Services/AdminAuthService.cs"
    if ($remoteAuth.Code -eq 0 -and
        $remoteAuth.Text -match 'FromBase64String\s*\(\s*"[A-Za-z0-9+/]{16,}={0,2}"') {
      Write-Host ''
      Write-Host 'NOTE: the copy already on GitHub contains the old admin salt and hash.' -ForegroundColor Red
      Write-Host 'Replacing the branch hides it but does not guarantee it is gone. Run' -ForegroundColor Red
      Write-Host 'tools\new-admin-credential.ps1 with a NEW password afterwards.' -ForegroundColor Red
    }
  }
  else {
    Write-Host ''
    Write-Host "There is nothing on $remoteRef yet, so nothing will be lost." -ForegroundColor Yellow
  }

  Write-Host ''
  $answer = Read-Host "Type  $Branch  to confirm"
  if ($answer -cne $Branch) { Fail 'Not confirmed. Nothing was pushed.' }
  $push = Invoke-Git push --force-with-lease -u origin $Branch
}
elseif ($upstream.Code -eq 0 -and $upstream.Text) {
  $push = Invoke-Git push
}
else {
  $push = Invoke-Git push -u origin $Branch
}

if ($push.Code -ne 0) {
  # §244. Telling someone who just used -Force to try -Force is how a person
  # ends up running the same command four times. The advice depends on which
  # push was refused.
  if ($Force) {
    Fail @"
The force push was refused.

git said:
$($push.Text)

The remote moved between the fetch and the push, which is exactly what the
lease is there to catch. Run the same command again - it will fetch the new
state and show you what it is about to replace.

If it keeps happening, something else is pushing to this branch.
"@
  }

  Fail @"
The push was rejected.

git said:
$($push.Text)

If this is the first push to a repository you have been dragging files into,
GitHub already has commits this machine does not, and git will not overwrite
them by accident.

Your disk is the real version, so replace what is up there:

  .\tools\git-save.ps1 -Force

It fetches first, shows you exactly what it is about to replace, and asks you
to confirm before it does anything.
"@
}

Write-Host ''
Write-Host "Pushed to origin/$Branch." -ForegroundColor Green
Write-Host ''
