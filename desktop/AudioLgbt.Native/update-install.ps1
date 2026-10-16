param([Parameter(Mandatory=$true)][string]$Plan)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Security
[Windows.Forms.Application]::EnableVisualStyles()
$folder = Split-Path -LiteralPath $Plan
$plain = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($Plan),$null,[Security.Cryptography.DataProtectionScope]::CurrentUser)
$data = [Text.Encoding]::UTF8.GetString($plain) | ConvertFrom-Json
$form = New-Object Windows.Forms.Form
$form.Text = 'audio.lgbt — обновление'
$form.ClientSize = New-Object Drawing.Size(520,360)
$form.StartPosition = 'Manual'
$form.Location = New-Object Drawing.Point([int]$data.Left,[int]$data.Top)
$form.FormBorderStyle = 'FixedSingle'
$form.MaximizeBox = $false
$form.BackColor = [Drawing.Color]::FromArgb(16,16,16)
$form.ForeColor = [Drawing.Color]::FromArgb(244,244,244)
$form.Font = New-Object Drawing.Font('Segoe UI',10)
$form.Icon = New-Object Drawing.Icon((Join-Path $folder 'app.ico'))
$logo = New-Object Windows.Forms.PictureBox
$logo.Image = $form.Icon.ToBitmap()
$logo.SizeMode = 'Zoom'
$logo.SetBounds(228,40,64,64)
$form.Controls.Add($logo)
function Add-Label($text,$top,$size) {
    $label = New-Object Windows.Forms.Label
    $label.Text = $text
    $label.Font = New-Object Drawing.Font('Segoe UI',$size)
    $label.TextAlign = 'MiddleCenter'
    $label.SetBounds(20,$top,480,35)
    $form.Controls.Add($label)
    return $label
}
$brand = Add-Label 'audio.lgbt' 112 15
$heading = Add-Label 'Устанавливаем обновление' 164 17
$detail = Add-Label 'audio.lgbt откроется автоматически' 253 10
$detail.ForeColor = [Drawing.Color]::FromArgb(162,162,162)
$track = New-Object Windows.Forms.Panel
$track.BackColor = [Drawing.Color]::FromArgb(32,32,32)
$track.SetBounds(52,231,416,6)
$fill = New-Object Windows.Forms.Panel
$fill.BackColor = $form.ForeColor
$fill.SetBounds(0,0,138,6)
$track.Controls.Add($fill)
$form.Controls.Add($track)
$continue = New-Object Windows.Forms.Button
$continue.Text = 'Продолжить'
$continue.FlatStyle = 'Flat'
$continue.FlatAppearance.BorderSize = 0
$continue.BackColor = [Drawing.Color]::FromArgb(32,32,32)
$continue.SetBounds(163,300,194,42)
$continue.Visible = $false
$form.Controls.Add($continue)
$script:completed = $false
$script:failed = $false
$script:setup = $null
$script:phase = 'waiting'
$script:started = [DateTime]::UtcNow
$script:mutex = New-Object Threading.Mutex($true,('Local\audio.lgbt-update-' + [Environment]::UserName))
function Launch-App {
    # Quote arguments according to the Windows command-line convention.
    $quoted = @('--updated')
    foreach ($arg in @($data.Args)) { $quoted += '"' + ([regex]::Replace([regex]::Replace([string]$arg, '(\\*)"', '$1$1\"'), '(\\+)$', '$1$1')) + '"' }
    $script:mutex.Dispose()
    $null = Start-Process -FilePath ([string]$data.App) -ArgumentList ($quoted -join ' ')
}
$continue.Add_Click({ Launch-App; $script:completed = $true; $form.Close() })
$form.Add_FormClosing({ param($sender,$event) if (-not $script:completed -and -not $script:failed) { $event.Cancel = $true } })
$timer = New-Object Windows.Forms.Timer
$timer.Interval = 30
$timer.Add_Tick({
    $fill.Left = if ($fill.Left -gt $track.Width) { -$fill.Width } else { $fill.Left + 6 }
    try {
        if ($script:failed) { return }
        if (Test-Path -LiteralPath (Join-Path $folder 'cancel')) { throw 'Update cancelled.' }
        if ($script:phase -eq 'waiting') {
            if (Get-Process -Id ([int]$data.ParentId) -ErrorAction SilentlyContinue) {
                if (([DateTime]::UtcNow - $script:started).TotalSeconds -gt 30) { throw 'Application did not exit.' }
                return
            }
            $sha = [Security.Cryptography.SHA256]::Create()
            $file = [IO.File]::OpenRead([string]$data.Installer)
            try { $actual = [BitConverter]::ToString($sha.ComputeHash($file)).Replace('-','') }
            finally { $file.Dispose(); $sha.Dispose() }
            if ($actual -ne $data.Sha256) { throw 'Installer checksum mismatch.' }
            $script:setup = Start-Process -FilePath ([string]$data.Installer) -ArgumentList '/S /UPDATE' -PassThru
            $script:phase = 'installing'
        } elseif ($script:phase -eq 'installing' -and $script:setup.HasExited) {
            if ($script:setup.ExitCode -ne 0) { throw 'Installer failed.' }
            $root = [Microsoft.Win32.RegistryKey]::OpenBaseKey([Microsoft.Win32.RegistryHive]::CurrentUser,[Microsoft.Win32.RegistryView]::Registry32)
            $key = $root.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Uninstall\audio.lgbt')
            try { if ($key.GetValue('DisplayVersion') -ne $data.Version) { throw 'Installed version mismatch.' } } finally { $key.Dispose(); $root.Dispose() }
            $heading.Text = 'Запускаем audio.lgbt'
            Launch-App
            $script:completed = $true
            $form.Close()
        }
    } catch {
        $script:failed = $true
        $heading.Text = 'Не удалось установить обновление'
        $detail.Text = 'Закройте другие окна audio.lgbt и повторите запуск.'
        $continue.Visible = $true
        $track.Visible = $false
        # No credentials, invitation URLs or process arguments are logged.
        [IO.File]::WriteAllText((Join-Path $folder 'error.txt'), $_.Exception.Message)
    }
})
$form.Add_Shown({ [IO.File]::WriteAllText((Join-Path $folder 'ready'), ''); $timer.Start() })
try { [Windows.Forms.Application]::Run($form) }
finally { $timer.Dispose(); $script:mutex.Dispose(); $logo.Image.Dispose(); $form.Dispose() }
