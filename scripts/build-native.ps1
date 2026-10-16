param([ValidateSet('x64','arm64')][string]$Architecture = 'x64')
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path $PSScriptRoot -Parent
Push-Location $projectRoot
$oldGoos = $env:GOOS
$oldGoarch = $env:GOARCH
$oldCgo = $env:CGO_ENABLED
try {
    $null = Get-Command dotnet -ErrorAction Stop
    $null = Get-Command go -ErrorAction Stop
    $output = Join-Path $projectRoot "dist/native-$Architecture"
    $dependencyDir = Join-Path $projectRoot '.cache'
    $null = New-Item -ItemType Directory -Force -Path $dependencyDir
    $archive = Join-Path $dependencyDir "ffmpeg-$Architecture.zip"
    if (-not (Test-Path -LiteralPath $archive)) {
        $platform = if ($Architecture -eq 'x64') { 'win64' } else { 'winarm64' }
        $releaseUrl = "https://github.com/aes-gcm/audio.lgbt/releases/download/dependencies-2026-10-15/ffmpeg-$Architecture.zip"
        try { Invoke-WebRequest -Uri $releaseUrl -OutFile $archive }
        catch { Invoke-WebRequest -Uri "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-n8.1-latest-$platform-lgpl-shared-8.1.zip" -OutFile $archive }
    }
    $hashes = @{
        arm64 = '207ee5796859b6a3ce033393a32ce6b2fe878b6d5753bb8cce979d9c6925e852'
        x64 = '66bfa3595d633afadf22be4ff5385cbdae8998006e61755e01333491e595b178'
    }
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hashes[$Architecture]) {
        throw 'FFmpeg archive checksum mismatch.'
    }
    dotnet publish desktop/AudioLgbt.Native -c Release -r "win-$Architecture" `
        -p:SelfContained=true -p:PublishSelfContained=true -p:PublishSingleFile=false -o $output --nologo
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
    $env:GOOS = 'windows'
    $env:GOARCH = if ($Architecture -eq 'x64') { 'amd64' } else { 'arm64' }
    $env:CGO_ENABLED = '0'
    go build -trimpath -ldflags '-s -w -H windowsgui' -o "$output/audio-engine.exe" ./cmd/audio-client
    if ($LASTEXITCODE -ne 0) { throw 'Go engine build failed' }
    & (Join-Path $PSScriptRoot 'build-capture.ps1') -Architecture $Architecture -Output $output
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($archive)
    try {
        $mediaPath = Join-Path $output 'media'
        $null = New-Item -ItemType Directory -Force -Path $mediaPath
        foreach ($name in @('avcodec-62.dll','avutil-60.dll','swresample-6.dll','swscale-9.dll')) {
            $entry = $zip.Entries | Where-Object { $_.FullName -like "*/bin/$name" } | Select-Object -First 1
            if ($null -eq $entry) { throw "Missing FFmpeg library: $name" }
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $mediaPath $name), $true)
        }
        $license = $zip.Entries | Where-Object { $_.FullName -like '*/LICENSE.txt' } | Select-Object -First 1
        if ($null -ne $license) {
            [IO.Compression.ZipFileExtensions]::ExtractToFile($license, (Join-Path $mediaPath 'FFmpeg-LICENSE.txt'), $true)
        }
    } finally { $zip.Dispose() }
    Write-Host "Unverified development build: $output/audio.lgbt.exe"
    Write-Host 'Create an installer with scripts/build-native-installer.ps1. Test the built client before distributing it.'
} finally {
    $env:GOOS = $oldGoos
    $env:GOARCH = $oldGoarch
    $env:CGO_ENABLED = $oldCgo
    Pop-Location
}
