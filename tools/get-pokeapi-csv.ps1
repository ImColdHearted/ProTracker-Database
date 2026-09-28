#requires -Version 5.1
<#
  §413. Downloads the six PokeAPI CSV files tools/build-pokemon-dex.py
  reads into tools/pokeapi/. Run it once from anywhere; it finds the
  repository from its own location. Then, from the repository root:

    python tools/build-pokemon-dex.py

  Source: https://github.com/PokeAPI/pokeapi/tree/master/data/v2/csv
  (BSD-3-Clause). About 2 MB in all.
#>
$ErrorActionPreference = 'Stop'

$base   = 'https://raw.githubusercontent.com/PokeAPI/pokeapi/master/data/v2/csv'
$target = Join-Path $PSScriptRoot 'pokeapi'
$files  = @(
    'abilities.csv',
    'ability_names.csv',
    'ability_flavor_text.csv',
    'pokemon_abilities.csv',
    'pokemon_abilities_past.csv',
    'pokemon_stats.csv'
)

New-Item -ItemType Directory -Force -Path $target | Out-Null
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

foreach ($file in $files) {
    $out = Join-Path $target $file
    Write-Host "Downloading $file ..."
    Invoke-WebRequest -Uri "$base/$file" -OutFile $out -UseBasicParsing
    Write-Host ("  {0:N0} bytes" -f (Get-Item $out).Length)
}

Write-Host ''
Write-Host "Done - the files are in $target."
Write-Host 'Next, from the repository root:  python tools/build-pokemon-dex.py'
