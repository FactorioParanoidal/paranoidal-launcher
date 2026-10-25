# Builds dist\ParanoidalLauncher.exe (.NET Framework 4.8 WinForms, MinGit embedded).
# Needs: Roslyn csc (Visual Studio / Build Tools) and .NET Framework 4.8 reference assemblies.
#
# Versions (README.md, section on versions): the version is set only in src\AppInfo.cs (AppInfo.Version). This script puts it into the
# exe manifest, requires CHANGELOG.md to start with its section and refuses to build a different exe under a version
# that is already built (dist\build-info.txt). -SameVersion allows that while the version was not given to anyone yet.
param([string]$Out = "", [switch]$SameVersion)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root
if (-not $Out) { $Out = Join-Path $root 'dist\ParanoidalLauncher.exe' }
$Out = [IO.Path]::GetFullPath($Out)
$utf8 = New-Object Text.UTF8Encoding $false

# ---- version: single source
$appInfo = [IO.File]::ReadAllText((Join-Path $root 'src\AppInfo.cs'), [Text.Encoding]::UTF8)
function Get-AppInfoConst([string]$name) {
    $m = [regex]::Match($appInfo, 'public const string ' + $name + ' = "([^"]+)";')
    if (-not $m.Success) { throw "AppInfo.$name not found in src\AppInfo.cs" }
    $m.Groups[1].Value
}
$version = Get-AppInfoConst 'Version'
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "AppInfo.Version must look like 1.2.3, got '$version'" }
$fileVersion = "$version.0"

$changelog = Join-Path $root 'CHANGELOG.md'
$top = if (Test-Path $changelog) { Select-String -Path $changelog -Pattern '^## (\S+)' -Encoding UTF8 | Select-Object -First 1 } else { $null }
if (-not $top -or $top.Matches[0].Groups[1].Value -ne $version) {
    throw "CHANGELOG.md must start with a section '## $version' (the version from src\AppInfo.cs) describing this version."
}

$minGit = Join-Path $root ('vendor\MinGit-' + (Get-AppInfoConst 'MinGitVersion') + '-bit.zip')
$minGitSha = 'da35e72aa21c005a5a0d298cfbae110bc1609a815730ea0dde84b01a1b3cd3be'
if ((Get-FileHash $minGit -Algorithm SHA256).Hash.ToLower() -ne $minGitSha) { throw "MinGit checksum mismatch: $minGit" }

$csc = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe',
                     'C:\Program Files (x86)\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe' -ErrorAction SilentlyContinue |
       Select-Object -First 1 -ExpandProperty FullName
if (-not $csc) { throw 'Roslyn csc.exe not found' }
$ref = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'

$obj = Join-Path $root 'obj'
New-Item -ItemType Directory -Force $obj, (Split-Path -Parent $Out) | Out-Null

# exe manifest with the version from AppInfo
$manifestSrc = [IO.File]::ReadAllText((Join-Path $root 'app.manifest'), [Text.Encoding]::UTF8)
$rx = [regex]'(<assemblyIdentity version=")[^"]*(" name="Paranoidal\.Launcher")'
if ($rx.Matches($manifestSrc).Count -ne 1) { throw 'app.manifest: assemblyIdentity of Paranoidal.Launcher not found' }
$manifest = Join-Path $obj 'app.manifest'
[IO.File]::WriteAllText($manifest, $rx.Replace($manifestSrc, '${1}' + $fileVersion + '${2}'), $utf8)

# application icon: PNG frames rendered from the Paranoidal gear
Add-Type -AssemblyName System.Drawing
$ico = Join-Path $obj 'app.ico'
$src = [Drawing.Image]::FromFile((Join-Path $root 'assets\gear.png'))
$frames = foreach ($s in 16, 24, 32, 48, 64, 128, 256) {
    $bmp = New-Object Drawing.Bitmap $s, $s
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = 'HighQualityBicubic'; $g.PixelOffsetMode = 'HighQuality'
    $g.DrawImage($src, 0, 0, $s, $s); $g.Dispose()
    $ms = New-Object IO.MemoryStream; $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png); $bmp.Dispose()
    , @($s, $ms.ToArray())
}
$src.Dispose()
$icoStream = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter $icoStream
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$frames.Count)
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    $s = $f[0]; $data = $f[1]
    $w.Write([byte]($s % 256)); $w.Write([byte]($s % 256)); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32); $w.Write([uint32]$data.Length); $w.Write([uint32]$offset)
    $offset += $data.Length
}
foreach ($f in $frames) { $w.Write([byte[]]$f[1]) }
$w.Flush(); [IO.File]::WriteAllBytes($ico, $icoStream.ToArray())

# compile into obj\ first: dist\ is replaced only when the version check passes
$tmpOut = Join-Path $obj 'ParanoidalLauncher.build.exe'
$refs = 'mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms', 'System.IO.Compression',
        'System.IO.Compression.FileSystem', 'System.Web.Extensions' | ForEach-Object { "/reference:$ref\$_.dll" }
$cargs = @('/nologo', '/noconfig', '/nostdlib+', '/target:winexe', '/platform:anycpu', '/optimize+', '/deterministic+',
          '/langversion:latest', '/utf8output', '/nowarn:1701', "/win32icon:$ico", "/win32manifest:$manifest",
          "/resource:$root\assets\splash.png,Paranoidal.splash.png",
          "/resource:$root\assets\gear.png,Paranoidal.gear.png",
          "/resource:$ico,Paranoidal.app.ico",
          "/resource:$minGit,Paranoidal.mingit.zip",
          "/out:$tmpOut") + $refs + (Get-ChildItem "$root\src\*.cs" | Sort-Object Name | ForEach-Object FullName)
& $csc @cargs
if ($LASTEXITCODE -ne 0) { throw "csc failed: $LASTEXITCODE" }

$fv = [Diagnostics.FileVersionInfo]::GetVersionInfo($tmpOut)
if ($fv.FileVersion -ne $fileVersion -or $fv.ProductVersion -ne $version) {
    throw "exe version is '$($fv.FileVersion)' / '$($fv.ProductVersion)', expected '$fileVersion' / '$version'"
}
$newSha = (Get-FileHash $tmpOut -Algorithm SHA256).Hash.ToLower()

# one version = one exe: the build is deterministic, so the same code gives the same bytes
$infoFile = Join-Path (Split-Path -Parent $Out) 'build-info.txt'
$prev = @{}
if (Test-Path $infoFile) {
    foreach ($line in [IO.File]::ReadAllLines($infoFile)) { $i = $line.IndexOf('='); if ($i -gt 0) { $prev[$line.Substring(0, $i)] = $line.Substring($i + 1) } }
}
if ($prev['version'] -eq $version -and $prev['exe_sha256'] -and $prev['exe_sha256'] -ne $newSha) {
    if (-not $SameVersion) {
        Remove-Item $tmpOut -Force
        throw ("Version $version is already built from other code (dist\build-info.txt). Raise AppInfo.Version in " +
               "src\AppInfo.cs and add its section to CHANGELOG.md. If $version was not given to anyone yet, " +
               "rebuild with -SameVersion.")
    }
    Write-Warning "Rebuilding $version from changed code (-SameVersion): the previous $version exe is replaced."
}

Move-Item -Force $tmpOut $Out
$lines = @("version=$version", "exe_sha256=$newSha", ("built=" + (Get-Date -Format 'yyyy-MM-dd HH:mm:ss')))
[IO.File]::WriteAllLines($infoFile, $lines, $utf8)
Get-Item $Out | Select-Object Name, Length, @{ n = 'Version'; e = { $_.VersionInfo.ProductVersion } }, @{ n = 'SHA256'; e = { $newSha } }
