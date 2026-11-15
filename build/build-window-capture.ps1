param(
    [Parameter(Mandatory)][string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
if (!(Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw 'Install Visual Studio 2022 or later with Desktop development with C++.' }
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$vs) { throw 'Visual Studio C++ x64 Build Tools not found.' }
$vc = Get-ChildItem -LiteralPath (Join-Path $vs 'VC/Tools/MSVC') -Directory |
    Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName 'bin/Hostx64/x64/cl.exe') } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1 -ExpandProperty FullName
if (!$vc) { throw 'MSVC x64 compiler not found.' }

$kits = (Get-ItemProperty 'HKLM:/SOFTWARE/WOW6432Node/Microsoft/Windows Kits/Installed Roots' -Name KitsRoot10).KitsRoot10
$include = Get-ChildItem -LiteralPath (Join-Path $kits 'Include') -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and [version]$_.Name -ge [version]'10.0.19041.0' -and
        (Test-Path -LiteralPath (Join-Path $_.FullName 'cppwinrt/winrt/Windows.Graphics.Capture.h')) -and
        (Test-Path -LiteralPath (Join-Path $kits "Lib/$($_.Name)/um/x64/windowsapp.lib")) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (!$include) { throw 'Windows SDK with C++/WinRT headers and x64 libraries not found.' }
$lib = Join-Path $kits "Lib/$($include.Name)"
$inc = $include.FullName
$destination = [IO.Path]::GetFullPath($OutputDirectory)
$null = New-Item -ItemType Directory -Path $destination -Force
$binary = Join-Path $destination 'dalamud-mcp-capture.exe'
$source = Join-Path $PSScriptRoot '../src/WindowCapture/WindowCapture.cpp'
$options = @(
    '/nologo', '/std:c++20', '/EHsc', '/MT', '/O2', '/W4', '/guard:cf',
    "/I$vc/include", "/I$inc/cppwinrt", "/I$inc/um", "/I$inc/shared", "/I$inc/ucrt", "/I$inc/winrt", "/Fe:$binary", $source,
    '/link', "/LIBPATH:$vc/lib/x64", "/LIBPATH:$lib/um/x64", "/LIBPATH:$lib/ucrt/x64",
    '/DEPENDENTLOADFLAG:0x800', 'user32.lib', 'dwmapi.lib', 'd3d11.lib', 'windowsapp.lib', 'ole32.lib', 'windowscodecs.lib'
)
Push-Location -LiteralPath $destination
try {
    & (Join-Path $vc 'bin/Hostx64/x64/cl.exe') @options
    if ($LASTEXITCODE -ne 0) { throw "Window capture worker build failed (exit $LASTEXITCODE)." }
    (Get-FileHash -LiteralPath $binary -Algorithm SHA256).Hash |
        Set-Content -LiteralPath (Join-Path $destination 'window-capture.sha256') -Encoding ascii
} finally { Pop-Location }
