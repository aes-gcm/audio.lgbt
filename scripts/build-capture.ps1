param([ValidateSet('x64','arm64')][string]$Architecture = 'x64', [string]$Output = '')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (!$Output) { $Output = Join-Path $projectRoot "dist/native-$Architecture" }
$vswhere = "${env:ProgramFiles(x86)}/Microsoft Visual Studio/Installer/vswhere.exe"
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (!$installation) { throw 'Visual Studio C++ build tools are required.' }
$devshell = Join-Path $installation 'Common7/Tools/Microsoft.VisualStudio.DevShell.dll'
Import-Module $devshell
Enter-VsDevShell -VsInstallPath $installation -SkipAutomaticLocation -DevCmdArguments "-arch=$Architecture -host_arch=x64"
$null = New-Item -ItemType Directory -Force $Output
$source = Join-Path $projectRoot 'desktop/AudioLgbt.Capture/capture.cpp'
& cl.exe /nologo /std:c++20 /EHsc /O2 /MT /LD /DUNICODE /D_UNICODE /DNTDDI_VERSION=0x0A00000C $source "/Fo$Output/capture.obj" /link "/OUT:$Output/audio-capture.dll" d3d11.lib dxgi.lib windowsapp.lib mmdevapi.lib ole32.lib
if ($LASTEXITCODE -ne 0) { throw 'Native capture build failed' }
