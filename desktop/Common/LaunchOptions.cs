namespace AudioLgbt;

internal sealed record LaunchOptions(Uri Server, Uri Start)
{
    public static LaunchOptions Parse(string[] args)
    {
        var server = new Uri("https://audio.lgbt/");
        string? invitation = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--server" && i + 1 < args.Length)
            {
                if (!Uri.TryCreate(args[++i], UriKind.Absolute, out var parsed) || !ValidServer(parsed))
                    throw new ArgumentException("Адрес сервера должен начинаться с https://. HTTP разрешён только для localhost.");
                server = parsed;
            }
            else if (args[i].StartsWith("--", StringComparison.Ordinal) || invitation != null)
                throw new ArgumentException("Запуск: audio.lgbt.exe [--server https://ваш-сервер/] [ссылка на звонок]");
            else invitation = args[i];
        }
        var start = server;
        if (invitation != null && (!Uri.TryCreate(invitation, UriKind.Absolute, out start) || !IsInvitation(server, start)))
            throw new ArgumentException("Нужна полная ссылка на звонок с выбранного сервера, включая часть после #.");
        return new LaunchOptions(server, start!);
    }

    private static bool ValidServer(Uri uri) =>
        (uri.Scheme == "https" || (uri.Scheme == "http" && IsLocal(uri))) &&
        uri.UserInfo.Length == 0 && uri.AbsolutePath == "/" && uri.Query.Length == 0 && uri.Fragment.Length == 0;

    private static bool IsLocal(Uri uri) => uri.Host is "localhost" or "127.0.0.1" or "[::1]";

    public static bool SameOrigin(Uri server, Uri uri) => uri.Scheme == server.Scheme &&
        uri.IdnHost == server.IdnHost && uri.Port == server.Port && uri.UserInfo.Length == 0;

    public static bool IsInvitation(Uri server, Uri uri)
    {
        if (!SameOrigin(server, uri) || uri.AbsolutePath != "/" || uri.Fragment.Length == 0) return false;
        var key = uri.Fragment[1..].Replace('-', '+').Replace('_', '/');
        try { return Convert.FromBase64String(key.PadRight((key.Length + 3) / 4 * 4, '=')).Length == 16; }
        catch (FormatException) { return false; }
    }
}
