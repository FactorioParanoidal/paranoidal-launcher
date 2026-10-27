# Writes release.json for a launcher exe (the manifest the launcher reads from its GitHub Releases) and the release notes.
# Usage: tools\release-manifest.ps1 -Exe dist\ParanoidalLauncher.exe -OutDir dist [-Changelog CHANGELOG.md]
# release.json (same format as the guide's, README «Где лаунчер ищет обновления»):
#   {"schema":1,"product":"launcher","version":"1.2.0","date":"2026-10-28","file":"ParanoidalLauncher.exe",
#    "size":<bytes>,"sha256":"<hex>","changes":[{"version":"1.2.0","date":"...","text":"<markdown>"}, ...]}  newest first
# The version is the exe's product version; CHANGELOG.md must start with its section.
param([Parameter(Mandatory = $true)][string]$Exe, [Parameter(Mandatory = $true)][string]$OutDir, [string]$Changelog = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Changelog) { $Changelog = Join-Path $root 'CHANGELOG.md' }
$Exe = [IO.Path]::GetFullPath($Exe)
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Exe).ProductVersion
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "exe product version '$version' is not x.y.z" }

$text = [IO.File]::ReadAllText($Changelog, [Text.Encoding]::UTF8) -replace "`r`n", "`n"
$heads = [regex]::Matches($text, '(?m)^## (\d+\.\d+\.\d+)(?:\s+—\s+(\d{4}-\d\d-\d\d))?[ \t]*$')
$changes = @()
for ($i = 0; $i -lt $heads.Count; $i++) {
    $h = $heads[$i]
    $end = if ($i + 1 -lt $heads.Count) { $heads[$i + 1].Index } else { $text.Length }
    $body = $text.Substring($h.Index + $h.Length, $end - $h.Index - $h.Length).Trim()
    $changes += [ordered]@{ version = $h.Groups[1].Value; date = $h.Groups[2].Value; text = $body }
}
if ($changes.Count -eq 0 -or $changes[0].version -ne $version) { throw "CHANGELOG.md must start with '## $version — YYYY-MM-DD'" }
if ($changes[0].text -match '\(черновик\)') { throw "CHANGELOG.md section $version is still a draft" }

$bytes = [IO.File]::ReadAllBytes($Exe)
$sha = (Get-FileHash $Exe -Algorithm SHA256).Hash.ToLower()
$manifest = [ordered]@{
    schema  = 1
    product = 'launcher'
    version = $version
    date    = $changes[0].date
    file    = 'ParanoidalLauncher.exe'
    size    = $bytes.Length
    sha256  = $sha
    changes = $changes
}
New-Item -ItemType Directory -Force $OutDir | Out-Null
$utf8 = New-Object Text.UTF8Encoding $false
[IO.File]::WriteAllText((Join-Path $OutDir 'release.json'), ($manifest | ConvertTo-Json -Depth 5), $utf8)
$notes = $changes[0].text + "`n`nСкачать: ``ParanoidalLauncher.exe`` ниже. SHA-256: ``$sha``. Лаунчер 1.2.0 и новее обновляются сами; 1.1.0 и старше — скачайте вручную.`n"
[IO.File]::WriteAllText((Join-Path $OutDir 'release-notes.md'), $notes, $utf8)
Write-Output "release.json: launcher $version, $($bytes.Length) bytes, sha256 $sha, $($changes.Count) changelog sections"
