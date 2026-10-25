# Builds tools\UiShot.cs with every launcher source except Program.cs: obj\UiShot.exe (not shipped).
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$csc = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe',
                     'C:\Program Files (x86)\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe' -ErrorAction SilentlyContinue |
       Select-Object -First 1 -ExpandProperty FullName
if (-not $csc) { throw 'Roslyn csc.exe not found' }
$ref = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$refs = 'mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.Windows.Forms', 'System.IO.Compression',
        'System.IO.Compression.FileSystem', 'System.Web.Extensions' | ForEach-Object { "/reference:$ref\$_.dll" }
$files = Get-ChildItem "$root\src\*.cs" | Where-Object { $_.Name -ne 'Program.cs' } | ForEach-Object FullName
New-Item -ItemType Directory -Force "$root\obj" | Out-Null
& $csc /nologo /noconfig /nostdlib+ /nowarn:1701 /target:winexe /langversion:latest "/out:$root\obj\UiShot.exe" `
    "/resource:$root\assets\gear.png,Paranoidal.gear.png" "/resource:$root\assets\splash.png,Paranoidal.splash.png" @refs @files "$root\tools\UiShot.cs"
if ($LASTEXITCODE -ne 0) { throw 'UiShot build failed' }
