using AudioLgbt;

var key = Convert.ToBase64String(new byte[16]).TrimEnd('=').Replace('+', '-').Replace('/', '_');
void Check(bool value, string message) { if (!value) throw new Exception(message); }
void Reject(params string[] args) { try { LaunchOptions.Parse(args); } catch (ArgumentException) { return; } throw new Exception("Accepted unsafe or invalid launch arguments"); }
var defaults = LaunchOptions.Parse([]);
Check(defaults.Server.AbsoluteUri == "https://audio.lgbt/", "Default server");
Check(LaunchOptions.Parse([$"https://audio.lgbt/#{key}"]).Start.Fragment == "#"+key, "Fragment preserved locally");
Check(LaunchOptions.Parse(["--server", "http://localhost:8081/", $"http://localhost:8081/#{key}"]).Server.Port == 8081, "Local development");
Check(LaunchOptions.Parse(["--server", "https://call.example.org/"]).Server.Host == "call.example.org", "Self hosted server");
Reject("--server", "http://audio.lgbt/");
Reject("--server", "https://user:password@audio.lgbt/");
Reject("--server", "file:///tmp/test");
Reject("--server", "https://audio.lgbt/path");
Reject("--server", "https://audio.lgbt/#secret");
Reject("--server");
Reject($"https://audio.lgbt.evil.example/#{key}");
Reject($"https://audio.lgbt@evil.example/#{key}");
Reject($"https://audio.lgbt:444/#{key}");
Reject($"javascript:alert(1)#{key}");
Reject("https://audio.lgbt/#broken");
Reject("https://audio.lgbt/#"+Convert.ToBase64String(new byte[17]));
Reject($"https://audio.lgbt/other#{key}");
Console.WriteLine("PASS: defaults, server configuration, local development, invitation validation and origin isolation");
