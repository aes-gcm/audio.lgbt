param([string]$Nsis, [switch]$SkipBuild)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
if (-not $SkipBuild) { & (Join-Path $PSScriptRoot 'build-native.ps1') -Architecture x64 }
if (-not $Nsis) { $Nsis = (Get-Command makensis -ErrorAction Stop).Source }
$appDir = Join-Path $projectRoot 'dist/native-x64'
$manifestDir = Join-Path $projectRoot 'dist/native-installer'
$null = New-Item -ItemType Directory -Force -Path $manifestDir
$files = @(Get-ChildItem -LiteralPath $appDir -Recurse -File | Where-Object {
    $_.Name -notlike '*test*' -and $_.Extension -notin @('.pdb','.lib','.exp','.obj')
} | Sort-Object FullName)
foreach ($required in @('audio.lgbt.exe','audio-engine.exe','audio-capture.dll','media/avcodec-62.dll')) {
    if (-not (Test-Path -LiteralPath (Join-Path $appDir $required))) { throw "Missing native component: $required" }
}
$payload = [Collections.Generic.List[string]]::new()
$remove = [Collections.Generic.List[string]]::new()
$folders = [Collections.Generic.HashSet[string]]::new()
foreach ($file in $files) {
    $relative = $file.FullName.Substring($appDir.Length + 1)
    $folder = Split-Path $relative -Parent
    $payload.Add(('SetOutPath "$INSTDIR\{0}"' -f $folder))
    $payload.Add(('File "{0}"' -f $file.FullName.Replace('$','$$')))
    $remove.Add(('Delete "$INSTDIR\{0}"' -f $relative))
    while ($folder) { $null = $folders.Add($folder); $folder = Split-Path $folder -Parent }
}
foreach ($folder in ($folders | Sort-Object Length -Descending)) { $remove.Add(('RMDir "$INSTDIR\{0}"' -f $folder)) }
$payloadPath = Join-Path $manifestDir 'payload.nsh'
$removePath = Join-Path $manifestDir 'remove.nsh'
$payload | Set-Content -LiteralPath $payloadPath -Encoding UTF8
$remove | Set-Content -LiteralPath $removePath -Encoding UTF8
$output = Join-Path $projectRoot 'dist/audio.lgbt-Native-Preview-Setup.exe'
& $Nsis /V2 /INPUTCHARSET UTF8 "/DOUTPUT=$output" "/DPAYLOAD=$payloadPath" "/DREMOVE=$removePath" (Join-Path $projectRoot 'desktop/installer/audio-lgbt-native.nsi')
if ($LASTEXITCODE -ne 0) { throw 'Native installer build failed.' }
Get-FileHash -LiteralPath $output -Algorithm SHA256
