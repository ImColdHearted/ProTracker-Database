#requires -Version 5.1
<#
  §242. Creates or rotates the ProTracker master admin credential.
  §243 fixed it: the first version used an API that does not exist in Windows
  PowerShell, threw before writing anything, and left the app with no
  credential to check against. See below.

  Writes %LOCALAPPDATA%\ProTracker\Database\admin-credential.json, which is the
  ONLY place the app looks. That file is per-machine, is never committed, and
  is named in .gitignore in case a copy is ever dropped into the tree by hand.

  What is written is PBKDF2-HMAC-SHA256 over the password with a fresh random
  salt - never the password itself. The password is read as a SecureString, is
  held as plain text only for the moments the derivation and the self-test
  need it, and is wiped from memory afterwards. Nothing is echoed, nothing is
  logged.

  Run it once to set the credential up. Run it again any time to change the
  password - a new salt is generated every time, so two runs with the same
  password produce different files.

  PORTABILITY. This has to run under Windows PowerShell 5.1, which is .NET
  Framework, not .NET Core. Several convenient crypto helpers do not exist
  there:

      RandomNumberGenerator::Fill        .NET Core 2.1+   (this was the bug)
      RandomNumberGenerator::GetBytes    .NET 6+   static form
      Rfc2898DeriveBytes::Pbkdf2         .NET 6+   static form
      CryptographicOperations           .NET Core 2.1+

  The app itself targets net10.0 and uses the modern forms freely. This script
  must not. Everything below is available in .NET Framework 4.7.2 and in every
  later runtime.
#>

[CmdletBinding()]
param(
  # 210,000 - the OWASP-recommended figure for PBKDF2-SHA256. AdminAuthService
  # refuses anything under 100,000 or over 5,000,000.
  [int]$Iterations = 210000
)

$ErrorActionPreference = 'Stop'

function Fail($text) {
  Write-Host ''
  Write-Host $text -ForegroundColor Red
  Write-Host 'Nothing was written. The existing credential, if any, is unchanged.' -ForegroundColor Red
  Write-Host ''
  exit 1
}

if ($Iterations -lt 100000 -or $Iterations -gt 5000000) {
  Fail "Iterations must be between 100,000 and 5,000,000. The app rejects anything outside that."
}

$dir  = Join-Path $env:LOCALAPPDATA 'ProTracker\Database'
$path = Join-Path $dir 'admin-credential.json'

Write-Host ''
Write-Host 'ProTracker admin credential' -ForegroundColor Cyan
Write-Host "  PowerShell $($PSVersionTable.PSVersion) ($($PSVersionTable.PSEdition))"
Write-Host "  -> $path"
if (Test-Path -LiteralPath $path) {
  Write-Host '  A credential already exists here and will be REPLACED.' -ForegroundColor Yellow
}
Write-Host ''

$username = Read-Host 'Admin username'
if ([string]::IsNullOrWhiteSpace($username)) { Fail 'Username cannot be empty.' }

$secure1 = Read-Host 'Admin password' -AsSecureString
$secure2 = Read-Host 'Confirm password' -AsSecureString

# Marshal out of the SecureStrings, derive, write, prove it reads back, then
# wipe. The plain text exists only inside this block, and the finally runs
# even if any step throws.
$bstr1 = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure1)
$bstr2 = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure2)
try {
  $plain1 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr1)
  $plain2 = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr2)

  if ($plain1 -cne $plain2) { Fail 'The two passwords do not match.' }
  if ($plain1.Length -lt 8) { Fail 'Use at least 8 characters.' }

  # RandomNumberGenerator.Create().GetBytes(byte[]) - the portable form.
  # ::Fill() is .NET Core only and is what broke this script in §242.
  $salt = New-Object byte[] 16
  $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
  try { $rng.GetBytes($salt) } finally { $rng.Dispose() }

  # The instance constructor, not the static Pbkdf2 helper. The four-argument
  # form needs .NET Framework 4.7.2, which every supported Windows has.
  function Derive([string]$secret, [byte[]]$withSalt, [int]$rounds) {
    $bytes = [Text.Encoding]::UTF8.GetBytes($secret)
    try {
      $kdf = New-Object System.Security.Cryptography.Rfc2898DeriveBytes(
        $bytes, $withSalt, $rounds,
        [System.Security.Cryptography.HashAlgorithmName]::SHA256)
      try { return $kdf.GetBytes(32) } finally { $kdf.Dispose() }
    }
    finally { [Array]::Clear($bytes, 0, $bytes.Length) }
  }

  try {
    $hash = Derive $plain1 $salt $Iterations
  }
  catch {
    Fail "Could not derive the key: $($_.Exception.Message)`nThis needs .NET Framework 4.7.2 or later, or PowerShell 7."
  }

  $json = [ordered]@{
    username   = $username
    salt       = [Convert]::ToBase64String($salt)
    hash       = [Convert]::ToBase64String($hash)
    iterations = $Iterations
  } | ConvertTo-Json

  New-Item -ItemType Directory -Path $dir -Force | Out-Null

  # Written without a BOM: the app parses it as JSON, and a BOM is one more
  # thing that can go wrong for no benefit.
  [IO.File]::WriteAllText($path, $json, (New-Object Text.UTF8Encoding($false)))

  # ---- self test -------------------------------------------------------
  # §243. The first version wrote nothing and said nothing useful when it
  # failed. Now the file is read back off the disk and the password is
  # verified against it exactly the way the app will, before this script is
  # willing to claim it worked.
  if (-not (Test-Path -LiteralPath $path)) { Fail "The file was not created at $path" }

  $readBack = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
  $checkSalt = [Convert]::FromBase64String($readBack.salt)
  $checkHash = [Convert]::FromBase64String($readBack.hash)

  if ($readBack.username -cne $username)   { Fail 'The file read back with the wrong username.' }
  if ($checkSalt.Length -ne 16)            { Fail 'The file read back with a bad salt.' }
  if ($checkHash.Length -ne 32)            { Fail 'The file read back with a bad hash.' }
  if ([int]$readBack.iterations -ne $Iterations) { Fail 'The file read back with the wrong iteration count.' }

  $again = Derive $plain1 $checkSalt ([int]$readBack.iterations)
  $same = $true
  for ($i = 0; $i -lt 32; $i++) { if ($again[$i] -ne $checkHash[$i]) { $same = $false } }
  if (-not $same) { Fail 'The password did not verify against the file that was just written.' }

  $wrong = Derive ($plain1 + 'x') $checkSalt ([int]$readBack.iterations)
  $differs = $false
  for ($i = 0; $i -lt 32; $i++) { if ($wrong[$i] -ne $checkHash[$i]) { $differs = $true } }
  if (-not $differs) { Fail 'A wrong password verified against the file. Something is badly wrong.' }
}
finally {
  [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr1)
  [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr2)
  $plain1 = $null; $plain2 = $null
  [GC]::Collect()
}

Write-Host ''
Write-Host "Written and verified: $path" -ForegroundColor Green
Write-Host 'The password was checked against the file the same way the app will.' -ForegroundColor Green
Write-Host 'Restart ProTracker for it to take effect.' -ForegroundColor Green
Write-Host 'Nothing about this credential is in the repository.' -ForegroundColor Green
Write-Host ''
