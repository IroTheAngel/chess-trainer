// Repertoire Trainer for Windows: a window around the trainer page, served from the app folder.
// Rebuilt from version 1.0 with one addition: F11 (or the page asking for full screen) fills the
// whole screen, and F11 or Esc brings the window back.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using Microsoft.Win32;

static class Program
{
    const string Name = "Repertoire Trainer";
    const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RepertoireTrainer";
    static readonly string InstallDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", Name);
    static readonly string DataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Name);
    static string InstalledExe { get { return Path.Combine(InstallDir, Name + ".exe"); } }

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (Array.IndexOf(args, "--uninstall") >= 0) { Uninstall(); return; }
        string here = Path.GetDirectoryName(Application.ExecutablePath);
        if (!string.Equals(Path.GetFullPath(here).TrimEnd('\\'), Path.GetFullPath(InstallDir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
        {
            if (Install(Array.IndexOf(args, "--quiet") >= 0)) Process.Start(InstalledExe);
            return;
        }
        new AppWindow().Run();
    }

    static bool Install(bool quiet)
    {
        if (!quiet && MessageBox.Show("Install Repertoire Trainer on this PC?\n\nIt adds a Start menu and a desktop shortcut.",
                "Repertoire Trainer setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return false;
        try
        {
            Directory.CreateDirectory(InstallDir);
            var asm = Assembly.GetExecutingAssembly();
            foreach (var res in asm.GetManifestResourceNames())
            {
                if (!res.StartsWith("payload/")) continue;
                string dest = Path.Combine(InstallDir, res.Substring("payload/".Length).Replace('/', '\\'));
                Directory.CreateDirectory(Path.GetDirectoryName(dest));
                using (var s = asm.GetManifestResourceStream(res))
                using (var f = File.Create(dest)) s.CopyTo(f);
            }
            File.Copy(Application.ExecutablePath, InstalledExe, true);
        }
        catch (IOException)
        {
            MessageBox.Show("Repertoire Trainer is open. Close it, then run setup again.", "Repertoire Trainer setup", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Setup didn't finish:\n" + ex.Message, "Repertoire Trainer setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk"));
        MakeShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk"));
        using (var k = Registry.CurrentUser.CreateSubKey(UninstallKey))
        {
            k.SetValue("DisplayName", Name);
            k.SetValue("DisplayIcon", InstalledExe + ",0");
            k.SetValue("DisplayVersion", "1.2");
            k.SetValue("Publisher", "IroTheAngel");
            k.SetValue("InstallLocation", InstallDir);
            k.SetValue("UninstallString", "\"" + InstalledExe + "\" --uninstall");
            k.SetValue("NoModify", 1, RegistryValueKind.DWord);
            k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            long kb = 0;
            foreach (var f in Directory.GetFiles(InstallDir, "*", SearchOption.AllDirectories)) kb += new FileInfo(f).Length / 1024;
            k.SetValue("EstimatedSize", (int)kb, RegistryValueKind.DWord);
        }
        return true;
    }

    static void MakeShortcut(string lnk)
    {
        try
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            object sc = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { lnk });
            Type st = sc.GetType();
            st.InvokeMember("TargetPath", BindingFlags.SetProperty, null, sc, new object[] { InstalledExe });
            st.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, sc, new object[] { InstallDir });
            st.InvokeMember("IconLocation", BindingFlags.SetProperty, null, sc, new object[] { InstalledExe + ",0" });
            st.InvokeMember("Description", BindingFlags.SetProperty, null, sc, new object[] { "Learn and drill your chess openings" });
            st.InvokeMember("Save", BindingFlags.InvokeMethod, null, sc, null);
        }
        catch { }
    }

    static void Uninstall()
    {
        if (MessageBox.Show("Remove Repertoire Trainer from this PC?\n\nYour saved progress stays on this PC, so it's still there if you install again.",
                Name, MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        foreach (var lnk in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), Name + ".lnk"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), Name + ".lnk") })
        { try { File.Delete(lnk); } catch { } }
        try { Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false); } catch { }
        var psi = new ProcessStartInfo("cmd.exe", "/c timeout /t 2 /nobreak >nul & rmdir /s /q \"" + InstallDir + "\"");
        psi.CreateNoWindow = true; psi.UseShellExecute = false;
        Process.Start(psi);
    }

    class AppWindow
    {
        const string Host = "https://trainer.local/";
        const string Site = "https://irotheangel.github.io/chess-trainer/";
        static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        static readonly Dictionary<string, Task<byte[]>> Pending = new Dictionary<string, Task<byte[]>>();
        static CoreWebView2Environment Env;
        static bool IndexChecked;

        Form form;
        WebView2 web;
        // Window state saved while in full screen, so leaving it puts the window back where it was.
        bool full;
        FormWindowState oldState;
        FormBorderStyle oldBorder;
        Rectangle oldBounds;

        // F11 toggles full screen inside the page (a key press counts as the user asking for it).
        const string F11Script =
            "document.addEventListener('keydown',function(e){if(e.key==='F11'){e.preventDefault();e.stopPropagation();" +
            "if(document.fullscreenElement){document.exitFullscreen().catch(function(){})}" +
            "else{document.documentElement.requestFullscreen().catch(function(){})}}},true);";

        public void Run()
        {
            ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            form = new Form();
            form.Text = Name;
            form.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            form.StartPosition = FormStartPosition.CenterScreen;
            Rectangle wa = Screen.PrimaryScreen.WorkingArea;
            form.ClientSize = new Size(Math.Min(1320, wa.Width - 60), Math.Min(900, wa.Height - 60));
            form.MinimumSize = new Size(380, 520);
            form.BackColor = Color.FromArgb(43, 41, 38);
            web = new WebView2();
            web.Dock = DockStyle.Fill;
            web.DefaultBackgroundColor = form.BackColor;
            form.Controls.Add(web);
            form.Shown += async (s, e) =>
            {
                try
                {
                    Env = await CoreWebView2Environment.CreateAsync(null, Path.Combine(DataDir, "WebView2"), null);
                    await web.EnsureCoreWebView2Async(Env);
                }
                catch (Exception ex)
                {
                    MessageBox.Show("This PC is missing Microsoft Edge WebView2, which Windows normally includes.\n\n" + ex.Message, Name);
                    form.Close();
                    return;
                }
                var core = web.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = true;
                core.AddWebResourceRequestedFilter(Host + "*", CoreWebView2WebResourceContext.All);
                core.AddWebResourceRequestedFilter("https://cdnjs.cloudflare.com/*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += OnRequest;
                core.NewWindowRequested += (s2, e2) => { e2.Handled = true; OpenOutside(e2.Uri); };
                core.NavigationStarting += (s2, e2) => { if (!e2.Uri.StartsWith(Host)) { e2.Cancel = true; OpenOutside(e2.Uri); } };
                core.DocumentTitleChanged += (s2, e2) => { form.Text = Name; };
                core.ContainsFullScreenElementChanged += (s2, e2) => SetFullScreen(core.ContainsFullScreenElement);
                await core.AddScriptToExecuteOnDocumentCreatedAsync(F11Script);
                core.Navigate(Host);
            };
            Application.Run(form);
        }

        void SetFullScreen(bool on)
        {
            if (on == full) return;
            full = on;
            if (on)
            {
                oldState = form.WindowState;
                oldBorder = form.FormBorderStyle;
                oldBounds = form.WindowState == FormWindowState.Normal ? form.Bounds : form.RestoreBounds;
                Screen sc = Screen.FromControl(form);
                form.WindowState = FormWindowState.Normal;
                form.FormBorderStyle = FormBorderStyle.None;
                form.Bounds = sc.Bounds;
                form.TopMost = true;
                form.TopMost = false;
                form.Activate();
            }
            else
            {
                form.FormBorderStyle = oldBorder;
                form.Bounds = oldBounds;
                form.WindowState = oldState;
            }
        }

        static void OpenOutside(string url)
        {
            if (!url.StartsWith("http://") && !url.StartsWith("https://")) return;
            try { Process.Start(url); } catch { }
        }

        static string TypeOf(string path)
        {
            switch (Path.GetExtension(path).ToLowerInvariant())
            {
                case ".html": return "text/html; charset=utf-8";
                case ".js": return "text/javascript; charset=utf-8";
                case ".json": return "application/json; charset=utf-8";
                case ".png": return "image/png";
                case ".svg": return "image/svg+xml";
                case ".mp3": return "audio/mpeg";
                case ".css": return "text/css; charset=utf-8";
                default: return "application/octet-stream";
            }
        }

        static void Respond(CoreWebView2WebResourceRequestedEventArgs e, byte[] data, string path)
        {
            if (data == null) { e.Response = Env.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain"); return; }
            e.Response = Env.CreateWebResourceResponse(new MemoryStream(data), 200, "OK",
                "Content-Type: " + TypeOf(path) + "\r\nCache-Control: no-cache\r\nAccess-Control-Allow-Origin: *");
        }

        static async void OnRequest(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            var deferral = e.GetDeferral();
            try
            {
                var u = new Uri(e.Request.Uri);
                if (u.Host == "cdnjs.cloudflare.com")
                {
                    if (u.AbsolutePath.EndsWith("/chess.min.js")) { Respond(e, Read(Path.Combine(InstallDir, "app", "chess.min.js")), "x.js"); return; }
                    string keep = Path.Combine(DataDir, "lib", Path.GetFileName(u.AbsolutePath));
                    byte[] lib = Read(keep) ?? await Fetch(u.ToString(), keep);
                    Respond(e, lib, keep);
                    return;
                }
                string path = Uri.UnescapeDataString(u.AbsolutePath).TrimStart('/');
                if (path == "") path = "index.html";
                if (path.Contains("..")) { Respond(e, null, path); return; }
                if (path.StartsWith("voice/")) { Respond(e, await Voice(path.Substring(6)), path); return; }
                Respond(e, Read(Path.Combine(InstallDir, "app", path.Replace('/', '\\'))), path);
            }
            catch
            {
                try { Respond(e, null, "x"); } catch { }
            }
            finally { deferral.Complete(); }
        }

        static byte[] Read(string path)
        {
            if (!File.Exists(path)) return null;
            try { return File.ReadAllBytes(path); } catch { return null; }
        }

        // Coach voice clips: kept on this PC after the first play. If the clip index changed, the kept clips are out of date.
        static async Task<byte[]> Voice(string name)
        {
            string dir = Path.Combine(DataDir, "voice");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, Path.GetFileName(name));
            if (name == "index.json")
            {
                if (!IndexChecked)
                {
                    IndexChecked = true;
                    byte[] fresh = null;
                    try { fresh = await Http.GetByteArrayAsync(Site + "voice/index.json"); } catch { }
                    if (fresh != null)
                    {
                        byte[] old = Read(file);
                        if (old == null || !Same(old, fresh))
                            foreach (var f in Directory.GetFiles(dir, "*.mp3")) { try { File.Delete(f); } catch { } }
                        File.WriteAllBytes(file, fresh);
                        return fresh;
                    }
                }
                return Read(file);
            }
            if (!name.EndsWith(".mp3")) return null;
            return Read(file) ?? await Fetch(Site + "voice/" + name, file);
        }

        static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static Task<byte[]> Fetch(string url, string keep)
        {
            Task<byte[]> t;
            if (!Pending.TryGetValue(url, out t))
            {
                t = FetchNow(url, keep);
                if (!t.IsCompleted) Pending[url] = t;
            }
            return t;
        }

        static async Task<byte[]> FetchNow(string url, string keep)
        {
            try
            {
                byte[] b = await Http.GetByteArrayAsync(url);
                Directory.CreateDirectory(Path.GetDirectoryName(keep));
                string tmp = keep + ".part";
                File.WriteAllBytes(tmp, b);
                if (File.Exists(keep)) File.Delete(keep);
                File.Move(tmp, keep);
                return b;
            }
            catch { return null; }
            finally { Pending.Remove(url); }
        }
    }
}
