using System.Security.Cryptography;
using System.Text.Json;

namespace AudioLgbt;

internal sealed record RecentRoom(string Id, string Title, string Invitation, DateTimeOffset LastJoined);

internal sealed class RoomHistory
{
    private readonly string path;
    private readonly List<RecentRoom> rooms = [];
    internal IReadOnlyList<RecentRoom> Rooms => rooms;
    internal RoomHistory(string? path = null)
    {
        this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "audio.lgbt", "recent-rooms.dat");
        try
        {
            var encrypted = File.ReadAllBytes(this.path);
            var plaintext = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try
            {
                var saved = JsonSerializer.Deserialize<List<RecentRoom>>(plaintext) ?? [];
                rooms.AddRange(saved.Where(r => !string.IsNullOrWhiteSpace(r.Id) && !string.IsNullOrWhiteSpace(r.Invitation))
                    .OrderByDescending(r => r.LastJoined).Take(12));
            }
            finally { CryptographicOperations.ZeroMemory(plaintext); }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException or JsonException) { }
    }

    internal void Remember(Uri server, string invitation, bool created)
    {
        if (!Uri.TryCreate(invitation, UriKind.Absolute, out var uri) || !LaunchOptions.IsInvitation(server, uri)) return;
        var previous = rooms.FirstOrDefault(r => r.Invitation == invitation);
        if (previous is not null) rooms.Remove(previous);
        rooms.Insert(0, new(previous?.Id ?? Guid.NewGuid().ToString("N"), previous?.Title ?? (created ? "Моя комната" : "Комната"), invitation, DateTimeOffset.Now));
        if (rooms.Count > 12) rooms.RemoveRange(12, rooms.Count - 12);
        Save();
    }

    internal void Rename(string id, string title)
    {
        int index = rooms.FindIndex(r => r.Id == id);
        title = title.Trim();
        if (index < 0 || title.Length == 0) return;
        rooms[index] = rooms[index] with { Title = title[..Math.Min(60, title.Length)] };
        Save();
    }
    internal void Remove(string id) { rooms.RemoveAll(r => r.Id == id); Save(); }
    private void Save()
    {
        var plaintext = JsonSerializer.SerializeToUtf8Bytes(rooms);
        try
        {
            var encrypted = ProtectedData.Protect(plaintext, null, DataProtectionScope.CurrentUser);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path + ".tmp", encrypted);
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or CryptographicException) { }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}

internal sealed class RecentRoomCard : Panel
{
    private readonly RecentRoom room;
    private readonly Action join;
    private bool hover;
    internal RecentRoomCard(RecentRoom room, Action join, Action rename, Action remove)
    {
        this.room = room; this.join = join;
        DoubleBuffered = true; Height = 76; Dock = DockStyle.Top; Margin = new Padding(0, 0, 0, 8);
        TabStop = true; Cursor = Cursors.Hand; AccessibleRole = AccessibleRole.PushButton; AccessibleName = "Войти: " + room.Title;
        var edit = Theme.Button(""); edit.Glyph = "edit"; edit.AccessibleName = "Переименовать: " + room.Title;
        var delete = Theme.Button(""); delete.Glyph = "close"; delete.AccessibleName = "Убрать из истории: " + room.Title;
        foreach (var b in new[] { edit, delete }) { b.AutoSize = false; b.MinimumSize = new Size(36, 36); b.Size = new Size(36, 36); Controls.Add(b); }
        edit.Click += (_, _) => rename(); delete.Click += (_, _) => remove();
        Resize += (_, _) => { edit.Location = new Point(Width - 86, 20); delete.Location = new Point(Width - 44, 20); };
        Click += (_, _) => join();
        MouseEnter += (_, _) => { hover = true; Invalidate(); }; MouseLeave += (_, _) => { hover = false; Invalidate(); };
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        Theme.Box(g, ClientRectangle, hover ? Theme.Raised : Theme.Surface, Focused ? Theme.Muted : Theme.Line);
        Icons.Draw(g, "people", new Rectangle(14, 26, 22, 22), Theme.Muted);
        using var title = Theme.Font(10.5f);
        using var date = Theme.Font(8.5f);
        TextRenderer.DrawText(g, room.Title, title, new Rectangle(48, 14, Math.Max(1, Width - 146), 24), Theme.Text, TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, room.LastJoined.LocalDateTime.ToString("dd MMM · HH:mm", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")), date, new Rectangle(48, 40, Math.Max(1, Width - 146), 20), Theme.Muted, TextFormatFlags.VerticalCenter);
    }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode is Keys.Enter or Keys.Space) { join(); e.Handled = e.SuppressKeyPress = true; } base.OnKeyDown(e); }
}
