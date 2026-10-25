# Builds the console test harness for SyncEngine (not shipped): obj\Harness.exe.
# -Src: another copy of src\ (e.g. an older launcher version, to test migrations).
param([string]$Src = "", [string]$Harness = "", [string]$Out = "")
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
if (-not $Src) { $Src = "$root\src" }
if (-not $Harness) { $Harness = "$root\tools\Harness.cs" }
if (-not $Out) { $Out = "$root\obj\Harness.exe" }
$csc = Get-ChildItem 'C:\Program Files\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe',
                     'C:\Program Files (x86)\Microsoft Visual Studio\*\*\MSBuild\Current\Bin\Roslyn\csc.exe' -ErrorAction SilentlyContinue |
       Select-Object -First 1 -ExpandProperty FullName
if (-not $csc) { throw 'Roslyn csc.exe not found' }
$ref = 'C:\Program Files (x86)\Reference Assemblies\Microsoft\Framework\.NETFramework\v4.8'
$refs = 'mscorlib', 'System', 'System.Core', 'System.Drawing', 'System.IO.Compression', 'System.IO.Compression.FileSystem',
        'System.Web.Extensions' | ForEach-Object { "/reference:$ref\$_.dll" }
# every engine file; Program.cs, MainForm.cs, Ui.cs and Dialogs.cs are the WinForms front end (the harness has its own Main)
$files = Get-ChildItem "$Src\*.cs" | Where-Object { 'Program.cs', 'MainForm.cs', 'Ui.cs', 'Dialogs.cs' -notcontains $_.Name } | ForEach-Object FullName
New-Item -ItemType Directory -Force (Split-Path -Parent $Out) | Out-Null
& $csc /nologo /noconfig /nostdlib+ /nowarn:1701 /target:exe /langversion:latest "/out:$Out" @refs @files $Harness
if ($LASTEXITCODE -ne 0) { throw 'harness build failed' }
