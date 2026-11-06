using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using System.Windows.Forms;
using Hv86.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Hv86.VmHost
{
    /// <summary>
    /// Окно одной ВМ: WebView2 с экраном v86.
    ///
    /// Окно - это и есть «Virtual Machine Connection»: пока его не показали,
    /// оно стоит за пределами экрана, а ВМ работает. Так одна и та же
    /// сущность может быть и фоновой ВМ, и консолью по требованию - без
    /// второго экземпляра эмулятора и без потоковой передачи кадров.
    ///
    /// Закрытие крестиком = скрыть, а не остановить ВМ (как в Hyper-V:
    /// закрытие консоли ВМ не выключает).
    /// </summary>
    public sealed class VmHostForm : Form
    {
        private readonly WebView2 _web;
        private readonly string _vmId;
        private readonly bool _replica;
        private bool _reallyClosing;
        private bool _offScreen = true;

        /// <summary>Сообщение из страницы консоли.</summary>
        public event Action<Dictionary<string, object>> PageMessage;

        public VmResourceServer ResourceServer { get; private set; }

        public VmHostForm(string vmId, string title, bool replica = false)
        {
            _vmId = vmId;
            _replica = replica;

            Text = "hv86 — " + title;
            BackColor = Color.FromArgb(0x05, 0x07, 0x0B);
            StartPosition = FormStartPosition.Manual;
            ClientSize = new Size(960, 700);
            MinimumSize = new Size(400, 300);
            ShowInTaskbar = false;
            FormBorderStyle = FormBorderStyle.Sizable;
            KeyPreview = false;

            try { Icon = Brand.Icon(32); }
            catch { }

            _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(_web);

            FormClosing += OnFormClosing;
        }

        // ----------------------------------------------------------------- init

        public async Task<bool> InitAsync(string consoleHtml)
        {
            try
            {
                // у реплики своя папка профиля: два процесса не могут делить
                // один user data folder у WebView2
                string userData = Path.Combine(Paths.WebViewDataDir,
                    "vm-" + _vmId + (_replica ? "-replica" : ""));
                Directory.CreateDirectory(userData);

                CoreWebView2Environment environment =
                    await CoreWebView2Environment.CreateAsync(null, userData);

                await _web.EnsureCoreWebView2Async(environment);

                CoreWebView2 core = _web.CoreWebView2;

                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.Settings.IsSwipeNavigationEnabled = false;

                // ничего внешнего: консоль - это наша страница и наши образы
                core.NewWindowRequested += delegate (object s, CoreWebView2NewWindowRequestedEventArgs e)
                {
                    e.Handled = true;
                };

                core.NavigationStarting += delegate (object s, CoreWebView2NavigationStartingEventArgs e)
                {
                    if (e.Uri != null && !e.Uri.StartsWith(VmResourceServer.Origin, StringComparison.OrdinalIgnoreCase))
                        e.Cancel = true;
                };

                core.WebMessageReceived += delegate (object s, CoreWebView2WebMessageReceivedEventArgs e)
                {
                    string json;

                    try { json = e.WebMessageAsJson; }
                    catch (Exception ex) { Log.Warn("VMHOST", "webmessage: " + ex.Message); return; }

                    Dictionary<string, object> message = Json.ParseObjectSafe(json);
                    if (message == null) return;

                    Action<Dictionary<string, object>> handler = PageMessage;
                    if (handler == null) return;

                    try { handler(message); }
                    catch (Exception ex) { Log.Exception("VMHOST", "обработчик страницы", ex); }
                };

                ResourceServer = new VmResourceServer(core) { ConsoleHtml = consoleHtml };
                core.Navigate(VmResourceServer.Origin + "/console.html");

                Log.Info("VMHOST", "webview2 " + core.Environment.BrowserVersionString + " готов");
                return true;
            }
            catch (Exception ex)
            {
                Log.Exception("VMHOST", "webview2 не запустился", ex);
                return false;
            }
        }

        public void Post(Dictionary<string, object> message)
        {
            if (message == null) return;

            string json = Json.Stringify(message);

            Action deliver = delegate
            {
                try
                {
                    CoreWebView2 core = _web.CoreWebView2;
                    if (core != null) core.PostWebMessageAsJson(json);
                }
                catch (Exception ex) { Log.Debug("VMHOST", "post: " + ex.Message); }
            };

            if (IsHandleCreated && InvokeRequired) BeginInvoke(deliver);
            else deliver();
        }

        // -------------------------------------------------------------- window

        /// <summary>Показать консоль: перенести окно на экран и поднять.</summary>
        public void ShowConsole()
        {
            if (InvokeRequired) { BeginInvoke(new Action(ShowConsole)); return; }

            if (_offScreen)
            {
                Rectangle area = Screen.PrimaryScreen == null ? new Rectangle(0, 0, 1024, 768) : Screen.PrimaryScreen.WorkingArea;

                _offScreen = false;
                ShowInTaskbar = true;
                StartPosition = FormStartPosition.Manual;
                Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + (area.Height - Height) / 2);

                if (!Visible) Show();
            }

            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;

            Activate();
            BringToFront();
        }

        /// <summary>Спрятать консоль: окно уезжает за пределы экрана, ВМ работает.</summary>
        public void HideConsole()
        {
            if (InvokeRequired) { BeginInvoke(new Action(HideConsole)); return; }

            _offScreen = true;
            ShowInTaskbar = false;

            // именно Move, а не Hide(): WebView2 на спрятанном окне теряет
            // видимость, а вместе с ней и кадры
            Location = new Point(-32000, -32000);

            Log.Info("VMHOST", "консоль скрыта, ВМ продолжает работу");
        }

        /// <summary>Поставить окно за пределы экрана до первого Show.</summary>
        public void PlaceOffScreen()
        {
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            ShowInTaskbar = false;
        }

        public void CloseForReal()
        {
            if (InvokeRequired) { BeginInvoke(new Action(CloseForReal)); return; }

            _reallyClosing = true;
            Close();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_reallyClosing) return;

            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                HideConsole();
                return;
            }

            _reallyClosing = true;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                try { ResourceServer?.Dispose(); } catch { }
                try { _web.Dispose(); } catch { }
            }

            base.Dispose(disposing);
        }
    }
}