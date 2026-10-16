using AudioLgbt;
using System.Text.Json;
using System.Reflection;

internal static class ServerChecks
{
    internal static int RunUi()
    {
        Application.SetHighDpiMode(HighDpiMode.DpiUnaware); Application.EnableVisualStyles();
        var root = Path.GetFullPath("dist/selfhost-check"); Directory.CreateDirectory(root);
        var prefs = Preferences.Load(Path.Combine(root,"ui-preferences.json")); prefs.Server = "https://audio.lgbt/"; prefs.Save();
        using var window = new MainWindow(LaunchOptions.Parse([]), prefs, new RoomHistory(Path.Combine(root,"ui-history.dat"))) { Location = new Point(-24000,-24000), StartPosition = FormStartPosition.Manual, ShowInTaskbar = false };
        int result = 0;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        ModalOverlay? Overlay() => (ModalOverlay?)typeof(MainWindow).GetField("modal",flags)!.GetValue(window);
        IEnumerable<Control> Children(Control parent) { foreach(Control c in parent.Controls) { yield return c; foreach(var child in Children(c)) yield return child; } }
        void Open() => typeof(MainWindow).GetMethod("ShowServer",flags)!.Invoke(window,null);
        async Task Wait(Func<bool> done) { for(int i=0;i<200;i++){if(done())return;await Task.Delay(30);}throw new Exception("UI timed out"); }
        window.Shown += async (_,_) => {
            try
            {
                Open(); var modal = Overlay()!;
                var address = Children(modal).OfType<ModernInput>().Single();
                var save = Children(modal).OfType<ModernButton>().Single(b=>b.Text=="Подключить сервер");
                address.Text = "http://not-local.example/"; save.PerformClick(); await Wait(()=>save.Enabled);
                if(prefs.Server!="https://audio.lgbt/" || Overlay()!=modal)throw new Exception("Invalid origin replaced current server");
                var target=Environment.GetEnvironmentVariable("BASE_URL")??"http://127.0.0.1:8082/";
                address.Text=target; save.PerformClick(); await Wait(()=>Overlay()==null);
                if(Preferences.Load(Path.Combine(root,"ui-preferences.json")).Server!=target)throw new Exception("Selected server not saved");
                Open(); await Wait(()=>Children(Overlay()!).OfType<ModernButton>().Any(b=>b.Visible&&b.Text=="Войти в аккаунт"));
                Children(Overlay()!).OfType<ModernButton>().Single(b=>b.Text=="Войти в аккаунт").PerformClick();
                var login=Overlay()!;var fields=Children(login).OfType<ModernInput>().ToArray();
                fields.Single(c=>c.AccessibleName=="Логин").Text="admin";
                var password=fields.Single(c=>c.AccessibleName=="Пароль");password.Text="local-test-password-123";
                if(!password.UseSystemPasswordChar)throw new Exception("Password is visible");
                using(var image=new Bitmap(login.Width,login.Height)){login.DrawToBitmap(image,new Rectangle(Point.Empty,image.Size));image.Save(Path.Combine(root,"login.png"));}
                Children(login).OfType<ModernButton>().Single(b=>b.Text=="Войти").PerformClick();
                await Wait(()=>Overlay()!=login);
                await Wait(()=>Children(Overlay()!).OfType<ModernButton>().Any(b=>b.Visible&&b.Text.StartsWith("Выйти из аккаунта")));
                if(Application.OpenForms.Count!=1)throw new Exception("Login created another window");
                Console.WriteLine("PASS native modal server validation/save/login and password masking");
            }
            catch(Exception e){Console.Error.WriteLine(e);result=1;}
            finally{window.Close();}
        };
        Application.Run(window);return result;
    }
    internal static async Task Run()
    {
        var server = new Uri(Environment.GetEnvironmentVariable("BASE_URL") ?? "http://127.0.0.1:8082/");
        using var account = new ServerAccount(server);
        if (!(await account.Status()).AuthenticationRequired) throw new Exception("Expected restricted test server");
        try { await account.Login("admin", "incorrect"); throw new Exception("Invalid password accepted"); }
        catch (InvalidOperationException) { }
        await account.Login("admin", "local-test-password-123");
        if (account.Token.Length != 43 || (await account.Status()).Username != "admin") throw new Exception("Native session missing");
        using var isolated = new ServerAccount(server);
        if (isolated.Token.Length != 0 || (await isolated.Status()).Username != "") throw new Exception("Session leaked between clients");
        string path = Path.GetFullPath("dist/selfhost-check/preferences.json"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var preferences = Preferences.Load(path); preferences.Server = server.AbsoluteUri; preferences.Save();
        if (Preferences.Load(path).Server != server.AbsoluteUri) throw new Exception("Server preference not persisted");
        if (File.ReadAllText(path).Contains(account.Token)) throw new Exception("Session stored in plain preferences");
        await account.Logout();
        if (account.Token.Length != 0 || (await account.Status()).Username != "") throw new Exception("Logout failed");
        Console.WriteLine("PASS native server discovery, login/error/logout, isolated sessions, URL persistence without credentials");
    }
}
