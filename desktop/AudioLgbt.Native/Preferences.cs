using System.Text.Json;
namespace AudioLgbt;
internal sealed class Preferences
{
    public string Name { get; set; } = "";
    public string Server { get; set; } = "https://audio.lgbt/";
    public int NameFormatVersion { get; set; }
    private string? storagePath;
    public int Microphone { get; set; } = -1;
    public string Speaker { get; set; } = "";
    public string Camera { get; set; } = "";
    public int MicGain { get; set; } = 100;
    public int Volume { get; set; } = 100;
    public bool NoiseSuppression { get; set; } = true;
    public bool Relay { get; set; }
    private static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "audio.lgbt", "preferences.json");
    public static Preferences Load(string? path = null)
    {
        Preferences result;
        try { result = JsonSerializer.Deserialize<Preferences>(File.ReadAllText(path ?? FilePath)) ?? new(); }
        catch { result = new(); }
        result.storagePath = path;
        if (result.NameFormatVersion == 0)
        {

            if (result.Name is "Windows · нативный клиент" or "Гость") result.Name = "";
            result.NameFormatVersion = 1;
            result.Save();
        }
        return result;
    }
    public void Save() { try { var path=storagePath ?? FilePath; Directory.CreateDirectory(Path.GetDirectoryName(path)!); var temporary = path + ".tmp"; File.WriteAllText(temporary, JsonSerializer.Serialize(this)); File.Move(temporary, path, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { } }
}
