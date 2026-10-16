using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;

namespace AudioLgbt;

internal sealed record ServerStatus(bool AuthenticationRequired, string Username);
internal sealed class ServerAccount : IDisposable
{
    private readonly CookieContainer cookies = new();
    private readonly HttpClient http;
    internal Uri Server { get; }
    internal string Token => cookies.GetCookies(Server)["audio_session"]?.Value ?? "";
    internal ServerAccount(Uri server)
    {
        Server = server;
        http = new HttpClient(new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false }) { BaseAddress = server, Timeout = TimeSpan.FromSeconds(10), MaxResponseContentBufferSize = 8192 };
    }
    internal async Task<ServerStatus> Status()
    {
        using var response = await http.GetAsync("api/server");
        if (response.StatusCode == HttpStatusCode.NotFound) return new(false, "");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ServerStatus>() ?? throw new InvalidDataException("Сервер прислал неверный ответ.");
    }
    internal async Task Login(string username, string password)
    {
        using var response = await http.PostAsJsonAsync("api/login", new { username, password });
        if (!response.IsSuccessStatusCode)
        {
            using var data = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            throw new InvalidOperationException(data.RootElement.TryGetProperty("error", out var error) ? error.GetString() : "Не удалось войти.");
        }
    }
    internal async Task Logout()
    {
        using var response = await http.PostAsJsonAsync("api/logout", new { });
        response.EnsureSuccessStatusCode();
        foreach (Cookie cookie in cookies.GetCookies(Server)) cookie.Expired = true;
    }
    public void Dispose() => http.Dispose();
}
