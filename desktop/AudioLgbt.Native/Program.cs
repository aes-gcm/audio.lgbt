namespace AudioLgbt;
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show("Не удалось выполнить действие.\n" + e.Exception.Message, "audio.lgbt", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        using var instance = new Mutex(true, @"Local\audio.lgbt-" + Environment.UserName, out bool first);
        if (!first) return;
        try { using var updating = Mutex.OpenExisting(@"Local\audio.lgbt-update-" + Environment.UserName); return; }
        catch (WaitHandleCannotBeOpenedException) { }
        try
        {
            bool updated = args.Contains("--updated"); args = args.Where(a => a != "--updated").ToArray();
            var preferences = Preferences.Load();
            var serverArgs = args;
            if (!args.Contains("--server"))
            {
                try { _ = LaunchOptions.Parse(["--server", preferences.Server]); }
                catch (ArgumentException) { preferences.Server = "https://audio.lgbt/"; preferences.Save(); }
                serverArgs = ["--server", preferences.Server, .. args];
            }
            var options = LaunchOptions.Parse(serverArgs);
            if (!updated && AppUpdate.OfficialUpdates && AppUpdate.Installed)
            {
                using var update = new UpdateWindow(args);
                if (update.ShowDialog() != DialogResult.OK) return;
            }
            Application.Run(new MainWindow(options, preferences));
        }
        catch (ArgumentException e) { MessageBox.Show(e.Message, "audio.lgbt", MessageBoxButtons.OK, MessageBoxIcon.Information); }
    }
}
