using System.Diagnostics;
using System.Text.Json;
using System.Threading.Channels;

namespace AudioLgbt;

internal sealed class ClientProcess : IAsyncDisposable
{
    private Process? process;
    private readonly Channel<string> commands = Channel.CreateBounded<string>(new BoundedChannelOptions(128) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly CancellationTokenSource lifetime = new();
    public event Action<JsonElement>? Message;
    public event Action<string>? Failed;
    public int? ProcessId => process?.Id;

    public void Start(Uri server, string key, string name, bool relay, string authToken = "")
    {
        var path = Path.Combine(AppContext.BaseDirectory, "audio-engine.exe");
        process = new Process { StartInfo = new(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, StandardInputEncoding = new System.Text.UTF8Encoding(false), StandardOutputEncoding = System.Text.Encoding.UTF8 } };
        process.Start();
        _ = Read(); _ = Write();

        _ = process.StandardError.ReadToEndAsync(lifetime.Token);
        Send(new { type = "start", server = server.AbsoluteUri, key, name, relay, authToken });
    }
    public bool Send(object command) => commands.Writer.TryWrite(JsonSerializer.Serialize(command));
    public void Media(string source, byte[] data, int duration) => Send(new { type = "media", source, data = Convert.ToBase64String(data), duration });
    private async Task Write()
    {
        try { await foreach (var line in commands.Reader.ReadAllAsync(lifetime.Token)) { await process!.StandardInput.WriteLineAsync(line); await process.StandardInput.FlushAsync(lifetime.Token); } }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException) { if (!lifetime.IsCancellationRequested) Failed?.Invoke("Служба звонка остановилась. Подключитесь снова."); }
    }
    private async Task Read()
    {
        try
        {
            while (await process!.StandardOutput.ReadLineAsync(lifetime.Token) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                Message?.Invoke(document.RootElement.Clone());
            }
            if (!lifetime.IsCancellationRequested) Failed?.Invoke("Служба звонка остановилась. Подключитесь снова.");
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or JsonException) { if (!lifetime.IsCancellationRequested) Failed?.Invoke("Не удалось прочитать ответ службы звонка."); }
    }
    public async ValueTask DisposeAsync()
    {
        lifetime.Cancel(); commands.Writer.TryComplete();
        if (process is not null)
        {
            try { process.StandardInput.Close(); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2)); await process.WaitForExitAsync(timeout.Token); }
            catch { try { process.Kill(true); } catch { } }
            process.Dispose(); process = null;
        }
        lifetime.Dispose();
    }
}
