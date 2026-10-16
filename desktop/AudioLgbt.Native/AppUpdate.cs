using System.Diagnostics;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Win32;

namespace AudioLgbt;

internal sealed record UpdateRelease(string Version, string Architecture, string Url, long Size, string Sha256);
internal sealed record UpdateProgress(string Stage, long Received = 0, long Total = 0, double BytesPerSecond = 0);

internal sealed class AppUpdate : IDisposable
{
    internal static bool OfficialUpdates => typeof(AppUpdate).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Any(value => value.Key == "OfficialUpdates" && value.Value == "true");
    internal static readonly Uri Feed = new("https://audio.lgbt/downloads/windows-x64.json");
    internal static string CurrentVersion => typeof(AppUpdate).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion.Split('+')[0];
    private readonly HttpClient http;
    internal AppUpdate(HttpMessageHandler? handler = null) => http = new(handler ?? new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(30) };

    internal static bool Installed
    {
        get
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry32);
            using var key = root.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\audio.lgbt");
            return key?.GetValue("InstallLocation") is string path && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)), Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory), StringComparison.OrdinalIgnoreCase);
        }
    }
    internal static bool IsNewer(string candidate, string current)
    {
        static (Version Core, int Preview) Parse(string value)
        {
            var parts = value.Split("-preview.", StringSplitOptions.None);
            if (parts.Length > 2 || !Version.TryParse(parts[0], out var core) || core.Build < 0 || core.Revision >= 0) throw new InvalidDataException("Invalid release version.");
            int preview = int.MaxValue;
            if (parts.Length == 2 && (!int.TryParse(parts[1], out preview) || preview < 1)) throw new InvalidDataException("Invalid preview version.");
            return (core, preview);
        }
        var a = Parse(candidate); var b = Parse(current);
        return a.Core != b.Core ? a.Core > b.Core : a.Preview > b.Preview;
    }
    internal static void Validate(UpdateRelease release)
    {
        _ = IsNewer(release.Version, "0.0.0");
        if (release.Architecture != "x64" || RuntimeInformation.ProcessArchitecture != Architecture.X64 ||
            !Uri.TryCreate(release.Url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "audio.lgbt" || !uri.IsDefaultPort ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            uri.AbsolutePath != $"/downloads/audio.lgbt-{release.Version}-x64.exe" ||
            release.Size is < 1024 or > 300_000_000 || release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Invalid update manifest.");
    }
    internal async Task<UpdateRelease?> Check(CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(8));
        using var response = await http.GetAsync(Feed, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var source = await response.Content.ReadAsStreamAsync(timeout.Token);
        var bytes = new byte[8193]; int length = 0, count;
        while ((count = await source.ReadAsync(bytes.AsMemory(length), timeout.Token)) > 0) { length += count; if (length > 8192) throw new InvalidDataException("Manifest too large."); }
        var release = JsonSerializer.Deserialize<UpdateRelease>(bytes.AsSpan(0, length), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidDataException("Empty manifest.");
        Validate(release);
        return IsNewer(release.Version, CurrentVersion) ? release : null;
    }
    internal async Task<string> Download(UpdateRelease release, string directory, IProgress<UpdateProgress> progress, CancellationToken token)
    {
        Validate(release); Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "Setup.exe"), partial = path + ".partial";
        try
        {
            using var response = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, token); response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != release.Size) throw new InvalidDataException("Download size mismatch.");
            using var source = await response.Content.ReadAsStreamAsync(token);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 65536, true))
            {
                byte[] buffer = new byte[65536]; long received = 0; var clock = Stopwatch.StartNew(); double last = -1;
                while (true)
                {
                    using var idle = CancellationTokenSource.CreateLinkedTokenSource(token); idle.CancelAfter(TimeSpan.FromSeconds(30));
                    int read = await source.ReadAsync(buffer, idle.Token); if (read == 0) break;
                    received += read; if (received > release.Size) throw new InvalidDataException("Download too large.");
                    await file.WriteAsync(buffer.AsMemory(0, read), token); hash.AppendData(buffer, 0, read);
                    if (clock.Elapsed.TotalSeconds - last > .08 || received == release.Size) { last = clock.Elapsed.TotalSeconds; progress.Report(new("Скачиваем обновление", received, release.Size, received / Math.Max(.1, last))); }
                }
                if (received != release.Size || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(release.Sha256))) throw new InvalidDataException("Update checksum mismatch.");
            }
            File.Move(partial, path, true); return path;
        }
        catch { File.Delete(partial); throw; }
    }
    internal static async Task StartInstaller(string installer, UpdateRelease release, string[] args, Point location, CancellationToken token)
    {
        string folder = Path.GetDirectoryName(installer)!, script = Path.Combine(folder, "install.ps1"), plan = Path.Combine(folder, "install.dat"), ready = Path.Combine(folder, "ready");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "update-install.ps1"), script, true);
        using (var resource = typeof(AppUpdate).Assembly.GetManifestResourceStream("AudioLgbt.app.ico")!)
        using (var icon = File.Create(Path.Combine(folder, "app.ico"))) resource.CopyTo(icon);

        var planBytes = JsonSerializer.SerializeToUtf8Bytes(new { Installer = installer, release.Sha256, release.Version, ParentId = Environment.ProcessId, App = Environment.ProcessPath, Args = args, Left = location.X, Top = location.Y });
        File.WriteAllBytes(plan, ProtectedData.Protect(planBytes, null, DataProtectionScope.CurrentUser));
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe")) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-STA", "-File", script, "-Plan", plan }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start updater.");
        var watch = Stopwatch.StartNew();
        while (!File.Exists(ready))
        {
            if (process.HasExited || watch.Elapsed > TimeSpan.FromSeconds(20)) { File.WriteAllText(Path.Combine(folder, "cancel"), ""); throw new IOException("Updater did not start."); }
            await Task.Delay(100, token);
        }
    }
    public void Dispose() => http.Dispose();
}
