# Publishes the launcher version from src\AppInfo.cs as GitHub Release v<version> of FactorioParanoidal/paranoidal-launcher.
# Run only on the owner's explicit command (AGENTS.md section 7). Use -DryRun first: it does everything except tag/push/upload
# and builds into obj\dryrun\ (dist\ keeps the record of released exes).
#
#   powershell -ExecutionPolicy Bypass -File publish.ps1 -DryRun
#   powershell -ExecutionPolicy Bypass -File publish.ps1
#
# Steps: clean main, not behind origin -> no tag v<version> yet -> build.ps1 (deterministic, version rules) -> harness
# self-test -> tools\release-manifest.ps1 (release.json, release-notes.md) -> annotated tag -> push main and the tag ->
# gh release create (exe + release.json, notes = CHANGELOG section, marked latest) -> check that
# https://github.com/<repo>/releases/latest/download/release.json serves this version and SHA-256.
# The launcher (1.2.0+) finds its updates exactly there; players get the direct link
# https://github.com/FactorioParanoidal/paranoidal-launcher/releases/latest/download/ParanoidalLauncher.exe
param([switch]$DryRun)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
$repo = 'FactorioParanoidal/paranoidal-launcher'

function Invoke-Git {
    & git.exe @args
    if ($LASTEXITCODE -ne 0) { throw "git $args failed ($LASTEXITCODE)" }
}

$appInfo = [IO.File]::ReadAllText((Join-Path $root 'src\AppInfo.cs'), [Text.Encoding]::UTF8)
$version = [regex]::Match($appInfo, 'public const string Version = "([^"]+)";').Groups[1].Value
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'AppInfo.Version not found' }
$tag = "v$version"
Write-Output "== Paranoidal Launcher $version ($tag)"

# ---- repository state
$branch = (& git.exe rev-parse --abbrev-ref HEAD).Trim()
if ($branch -ne 'main') { throw "current branch is '$branch', expected main" }
$dirty = & git.exe status --porcelain
if ($dirty) {
    if ($DryRun) { Write-Warning ("working tree is not clean (allowed in -DryRun):`n" + ($dirty -join "`n")) }
    else { throw 'working tree is not clean: commit first' }
}
Invoke-Git fetch origin --tags --quiet
$behind = (& git.exe rev-list --count HEAD..origin/main).Trim()
if ($behind -ne '0') { throw "main is $behind commit(s) behind origin/main" }
if (& git.exe tag --list $tag) { throw "tag $tag exists locally: this version is published already (raise AppInfo.Version)" }
if (& git.exe ls-remote --tags origin "refs/tags/$tag") { throw "tag $tag exists on origin: this version is published already" }

# ---- build and checks
$outDir = if ($DryRun) { Join-Path $root 'obj\dryrun' } else { Join-Path $root 'dist' }
if ($DryRun -and (Test-Path $outDir)) { Remove-Item -Recurse -Force $outDir }
$exe = Join-Path $outDir 'ParanoidalLauncher.exe'
& powershell -ExecutionPolicy Bypass -File (Join-Path $root 'build.ps1') -Out $exe
if ($LASTEXITCODE -ne 0) { throw 'build.ps1 failed' }
& powershell -ExecutionPolicy Bypass -File (Join-Path $root 'tools\build-harness.ps1')
if ($LASTEXITCODE -ne 0) { throw 'harness build failed' }
$self = & (Join-Path $root 'obj\Harness.exe') --loaddiag-selftest (Join-Path $root 'tools\testdata\load-logs-2.0.77.zip')
if (-not ($self -match 'SELFTEST OK')) { throw ("load diagnostics self-test failed:`n" + ($self -join "`n")) }
& powershell -ExecutionPolicy Bypass -File (Join-Path $root 'tools\release-manifest.ps1') -Exe $exe -OutDir $outDir
if ($LASTEXITCODE -ne 0) { throw 'release-manifest failed' }
$manifest = Get-Content (Join-Path $outDir 'release.json') -Raw -Encoding UTF8 | ConvertFrom-Json
if ($manifest.version -ne $version) { throw "release.json has version $($manifest.version)" }
Write-Output "exe sha256 $($manifest.sha256), $($manifest.size) bytes"

if ($DryRun) {
    $info = Join-Path $root 'dist\build-info.txt'
    if ((Test-Path $info) -and (Select-String -Path $info -Pattern ('^version=' + [regex]::Escape($version) + '$') -Quiet) -and
        -not (Select-String -Path $info -Pattern ('^exe_sha256=' + $manifest.sha256 + '$') -Quiet)) {
        Write-Warning "dist\build-info.txt already records another $version exe: the real run will refuse to build"
    }
    Write-Output "== DRY RUN: would tag $tag, push main and $tag, and create release $tag on $repo with the exe and release.json from $outDir"
    Get-Content (Join-Path $outDir 'release-notes.md') -Encoding UTF8
    exit 0
}

# ---- publish
if (-not $env:GH_TOKEN) {
    $cred = "protocol=https`nhost=github.com`nusername=ExpertLove`n`n" | & git.exe credential fill
    $env:GH_TOKEN = ($cred | Where-Object { $_ -like 'password=*' } | Select-Object -First 1) -replace '^password=', ''
    if (-not $env:GH_TOKEN) { throw 'no GitHub token from Git Credential Manager' }
}
Invoke-Git -c user.name=ExpertLove -c user.email=32990223+ExpertLove@users.noreply.github.com tag -a $tag -m "Paranoidal Launcher $version"
Invoke-Git push origin main
Invoke-Git push origin $tag
& gh release create $tag $exe (Join-Path $outDir 'release.json') --repo $repo --title "Paranoidal Launcher $version" `
    --notes-file (Join-Path $outDir 'release-notes.md') --latest --verify-tag
if ($LASTEXITCODE -ne 0) { throw 'gh release create failed (the tag is pushed: fix it and run gh release create by hand)' }

# ---- what the launcher will see
Start-Sleep -Seconds 5
$url = "https://github.com/$repo/releases/latest/download/release.json"
$live = (Invoke-WebRequest -UseBasicParsing $url).Content
if ($live -is [byte[]]) { $live = [Text.Encoding]::UTF8.GetString($live) }
$liveJson = $live | ConvertFrom-Json
if ($liveJson.version -ne $version -or $liveJson.sha256 -ne $manifest.sha256) { throw "latest release.json says $($liveJson.version) / $($liveJson.sha256)" }
Write-Output "== published: https://github.com/$repo/releases/tag/$tag"
Write-Output "   direct link: https://github.com/$repo/releases/latest/download/ParanoidalLauncher.exe"
