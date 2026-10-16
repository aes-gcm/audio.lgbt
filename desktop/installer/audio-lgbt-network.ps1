param(
    [Parameter(Mandatory=$true)][ValidateSet('Add','Remove')][string]$Mode,
    [Parameter(Mandatory=$true)][string]$EnginePath
)
$ErrorActionPreference = 'Stop'
try {
    $engine = [IO.Path]::GetFullPath($EnginePath)
    if ([IO.Path]::GetFileName($engine) -ne 'audio-engine.exe') { throw 'Unexpected network component.' }
    $admin = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
    if (-not $admin) {
        # One elevation request during install/uninstall, never from the call UI.
        $arguments = '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "{0}" -Mode {1} -EnginePath "{2}"' -f $PSCommandPath,$Mode,$engine
        $child = Start-Process -FilePath (Join-Path $PSHOME 'powershell.exe') -ArgumentList $arguments -Verb RunAs -WindowStyle Hidden -PassThru -Wait
        exit $child.ExitCode
    }
    $sha = [Security.Cryptography.SHA256]::Create()
    try { $suffix = ([BitConverter]::ToString($sha.ComputeHash([Text.Encoding]::UTF8.GetBytes($engine.ToLowerInvariant())))).Replace('-','').Substring(0,16) }
    finally { $sha.Dispose() }
    $name = 'audio.lgbt-RTC-UDP-' + $suffix
    $existing = Get-NetFirewallRule -Name $name -ErrorAction SilentlyContinue
    if ($Mode -eq 'Remove') {
        if ($existing) { $existing | Remove-NetFirewallRule }
    } else {
        if (-not (Test-Path -LiteralPath $engine -PathType Leaf)) { throw 'Network component is missing.' }
        if ($existing) {
            $existing | Set-NetFirewallRule -Enabled True -Direction Inbound -Action Allow -Profile Any
        } else {
            $null = New-NetFirewallRule -Name $name -DisplayName 'audio.lgbt — звонки' -Group 'audio.lgbt' -Description 'WebRTC media for the installed audio.lgbt client.' -Direction Inbound -Action Allow -Enabled True -Profile Any -Program $engine -Protocol UDP -EdgeTraversalPolicy Block
        }
        $rule = Get-NetFirewallRule -Name $name
        $filter = $rule | Get-NetFirewallApplicationFilter
        if ($rule.Enabled -ne 'True' -or $rule.Action -ne 'Allow' -or $filter.Program -ne $engine) { throw 'Network rule verification failed.' }
    }
    exit 0
} catch {
    Write-Error -ErrorAction Continue ('audio.lgbt network setup: ' + $_.Exception.Message)
    exit 1
}
