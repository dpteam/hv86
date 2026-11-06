using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Hv86.Core;
using Hv86.Vm;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Hv86.App
{
    /// <summary>
    /// Окно менеджера: Hyper-V-подобная компоновка, вся вёрстка - в web\manager.html.
    ///
    /// Окно не владеет ВМ: оно разговаривает со службой по named pipe. Закрытие
    /// окна ВМ не останавливает - они живут в процессах vmhost под службой.
    ///
    /// Почему NavigateToString: страница целиком в памяти, нет временных файлов
    /// и доступа JS к файловой системе. Ассеты v86 и медиа отдаются уже
    /// vmhost'ом по https://hv86.local/ - там это нужно для Range-запросов.
    /// </summary>
    public sealed class MainForm : Form
    {
        private readonly WebView2 _web;
        private readonly WebBridge _bridge;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly NotifyIcon _tray;
        private readonly SemaphoreSlim _clientGate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, HeadlessConsoleWindow> _headlessWindows =
            new Dictionary<string, HeadlessConsoleWindow>(StringComparer.Ordinal);

        private PipeClient _client;
        private bool _webReady;
        private bool _reallyClosing;
        private bool _trayHintShown;

        public MainForm()
        {
            Text = AppInfo.ProductName + " " + AppInfo.Version;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1120, 760);
            MinimumSize = new Size(900, 620);
            BackColor = Color.FromArgb(0x0C, 0x0F, 0x14);
            Font = new Font("Segoe UI", 9f);
            KeyPreview = true;

            try { Icon = Brand.Icon(32); }
            catch { }

            _bridge = new WebBridge(PostToPage);

            _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
            Controls.Add(_web);

            _tray = new NotifyIcon
            {
                Text = AppInfo.ProductName,
                Visible = true,
                Icon = Brand.Icon(16)
            };

            ContextMenuStrip menu = new ContextMenuStrip();
            menu.Items.Add("hv86", null, delegate { ShowWindow(); });
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { CloseForReal(); });
            _tray.ContextMenuStrip = menu;
            _tray.DoubleClick += delegate { ShowWindow(); };

            Shown += delegate { BeginInit(); };
            FormClosing += OnFormClosing;

            _timer = new System.Windows.Forms.Timer { Interval = 2000 };
            _timer.Tick += delegate { PushState(); };

            FormClosed += delegate
            {
                _timer.Stop();
                _timer.Dispose();
                try { _client?.Dispose(); } catch { }
                try { _tray.Visible = false; _tray.Dispose(); } catch { }
                try { _web.Dispose(); } catch { }
            };
        }

        // ------------------------------------------------------------- webview init

        private async void BeginInit()
        {
            RegisterActions();
            _timer.Start();

            try
            {
                Directory.CreateDirectory(Paths.WebViewDataDir);

                CoreWebView2Environment env = await CoreWebView2Environment.CreateAsync(null, Paths.WebViewDataDir);
                await _web.EnsureCoreWebView2Async(env);

                CoreWebView2 core = _web.CoreWebView2;

                core.Settings.AreDevToolsEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.Settings.IsZoomControlEnabled = false;
                core.Settings.IsPasswordAutosaveEnabled = false;
                core.Settings.IsGeneralAutofillEnabled = false;
                core.Settings.IsSwipeNavigationEnabled = false;

                core.NewWindowRequested += delegate (object s, CoreWebView2NewWindowRequestedEventArgs e)
                {
                    e.Handled = true;
                    OpenExternal(e.Uri);
                };

                core.NavigationStarting += delegate (object s, CoreWebView2NavigationStartingEventArgs e)
                {
                    if (e.Uri != null &&
                        !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) &&
                        !e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        e.Cancel = true;
                        OpenExternal(e.Uri);
                    }
                };

                core.WebMessageReceived += delegate (object s, CoreWebView2WebMessageReceivedEventArgs e)
                {
                    string json;
                    try { json = e.WebMessageAsJson; }
                    catch (Exception ex) { Log.Warn("UI", "webmessage: " + ex.Message); return; }

                    _bridge.Handle(json);
                };

                core.NavigateToString(BuildHtml());
                _webReady = true;

                Log.Info("UI", "webview2 " + core.Environment.BrowserVersionString + " готов");

                // первое состояние - сразу, не дожидаясь тика таймера
                PushState();
            }
            catch (Exception ex)
            {
                Log.Exception("UI", "webview2 не запустился", ex);

                MessageBox.Show(
                    "WebView2 не запустился:\r\n" + ex.Message +
                    "\r\n\r\nУстановите Microsoft Edge WebView2 Runtime:\r\n" +
                    "https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                    AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private string BuildHtml()
        {
            string html = Resources.Text(Resources.ManagerHtml);

            if (string.IsNullOrEmpty(html))
                return "<!doctype html><html><body style='font:14px Segoe UI;background:#0C0F14;color:#DDE6F5;padding:32px'>" +
                       "<h1>hv86</h1><p>Ресурсы интерфейса не вшиты. Пересоберите через tools\\build.cmd.</p></body></html>";

            string i18n = "{\"ru\":" + SafeText(Resources.I18nRu) + ",\"en\":" + SafeText(Resources.I18nEn) + "}";

            html = html.Replace("/*{{THEME}}*/", Resources.Text(Resources.ThemeCss));
            html = html.Replace("//{{JS}}", Resources.Text(Resources.ManagerJs));
            html = html.Replace("//{{I18N}}", "window.I18N = " + i18n + ";");
            html = html.Replace("{{VERSION}}", AppInfo.Version);

            return html;
        }

        private static string SafeText(string resource)
        {
            string text = Resources.Text(resource);
            return string.IsNullOrEmpty(text) ? "{}" : text;
        }

        // ------------------------------------------------------------- bridge

        private void RegisterActions()
        {
            _bridge.Register("ready", delegate { PushState(); });
            _bridge.Register("refresh", delegate { PushState(); });

            _bridge.Register("service.install", async delegate { await ServiceAction("install"); });
            _bridge.Register("service.start", async delegate { await ServiceAction("start"); });
            _bridge.Register("service.stop", async delegate { await ServiceAction("stop"); });
            _bridge.Register("service.uninstall", async delegate { await ServiceAction("uninstall"); });

            _bridge.Register("vm.create", async message =>
            {
                Dictionary<string, object> parameters = Clone(message);
                parameters.Remove("action");

                object result = await CallService("vm.create", parameters, 600000);
                _bridge.Send(WebBridge.Msg("toast", "text", "VM created", "kind", "ok"));
                _bridge.Send(WebBridge.Msg("created", "vm", result));

                await PushStateAsync();
            });

            _bridge.Register("vm.list", async delegate { await PushStateAsync(); });

            _bridge.Register("pick.file", delegate
            {
                string path = AskFile("Select ISO / disk image",
                    "Disk images (*.iso;*.img;*.bin;*.raw)|*.iso;*.img;*.bin;*.raw|All files (*.*)|*.*");

                if (!string.IsNullOrEmpty(path)) _bridge.Send(WebBridge.Msg("picked", "path", path));
            });

            _bridge.Register("pick.folder", delegate
            {
                string path = AskFolder("Select a folder to share with the VM");

                if (!string.IsNullOrEmpty(path)) _bridge.Send(WebBridge.Msg("picked", "path", path));
            });

            foreach (string command in new[] { "start", "stop", "pause", "resume", "reset", "hide", "delete" })
            {
                string local = command;
                _bridge.Register("vm." + local, async message =>
                {
                    await CallService("vm." + local, Json.Obj("id", Json.GetString(message, "id", "")), 120000);
                    await PushStateAsync();
                });
            }

            _bridge.Register("vm.snapshot", async message =>
            {
                object result = await CallService("vm.snapshot", Json.Obj(
                    "id", Json.GetString(message, "id", ""),
                    "name", Json.GetString(message, "name", "")), 600000);

                _bridge.Send(WebBridge.Msg("snapshot", "snapshot", result));
                await SendSnapshotsAsync(Json.GetString(message, "id", ""));
            });

            _bridge.Register("vm.snapshots", async message =>
            {
                await SendSnapshotsAsync(Json.GetString(message, "id", ""));
            });

            _bridge.Register("vm.revert", async message =>
            {
                await CallService("vm.revert", Json.Obj(
                    "id", Json.GetString(message, "id", ""),
                    "snapshot", Json.GetString(message, "snapshot", "")), 600000);

                _bridge.Send(WebBridge.Msg("toast", "text", "Reverted to snapshot", "kind", "ok"));
                await SendSnapshotsAsync(Json.GetString(message, "id", ""));
                await PushStateAsync();
            });

            _bridge.Register("vm.snapshot.delete", async message =>
            {
                await CallService("vm.snapshot.delete", Json.Obj(
                    "id", Json.GetString(message, "id", ""),
                    "snapshot", Json.GetString(message, "snapshot", "")), 120000);

                await SendSnapshotsAsync(Json.GetString(message, "id", ""));
            });

            _bridge.Register("vm.mount", async message =>
            {
                string kind = Json.GetString(message, "kind", "cdrom");

                string path = AskFile("Attach image / ISO",
                    "Disk images (*.iso;*.img;*.bin;*.raw)|*.iso;*.img;*.bin;*.raw|All files (*.*)|*.*");

                if (string.IsNullOrEmpty(path)) return;

                await CallService("vm.mount", Json.Obj(
                    "id", Json.GetString(message, "id", ""),
                    "path", path,
                    "kind", kind), 300000);

                _bridge.Send(WebBridge.Msg("toast", "text", "Image attached", "kind", "ok"));
                await PushStateAsync();
            });

            _bridge.Register("vm.eject", async message =>
            {
                await CallService("vm.eject", Json.Obj(
                    "id", Json.GetString(message, "id", ""),
                    "kind", Json.GetString(message, "kind", "cdrom")), 120000);

                await PushStateAsync();
            });

            // Консоль: у web-ВМ это окно vmhost, у headless - своё окно
            // интерфейса, которое цепляется к каналу движка напрямую.
            _bridge.Register("vm.console", async message =>
            {
                string id = Json.GetString(message, "id", "");

                Dictionary<string, object> view = await CallService("vm.get", Json.Obj("id", id), 15000)
                    as Dictionary<string, object>;

                string engine = Json.GetString(view, "engine", "web");
                string name = Json.GetString(view, "name", id);
                string state = Json.GetString(view, "state", "");

                if (engine == "headless")
                {
                    OpenHeadlessConsole(id, name);

                    if (state == "stopped" || state == "failed")
                        await CallService("vm.start", Json.Obj("id", id), 180000);
                }
                else
                {
                    await CallService("vm.console", Json.Obj("id", id), 180000);
                }

                await PushStateAsync();
            });

            _bridge.Register("open.data", delegate { OpenFolder(Paths.Root); });
            _bridge.Register("open.logs", delegate { OpenFolder(Paths.LogDir); });

            // ------------------------------------------------ настройки и архив

            _bridge.Register("vm.update", async message =>
            {
                Dictionary<string, object> parameters = Clone(message);
                parameters.Remove("action");

                await CallService("vm.update", parameters, 30000);

                _bridge.Send(WebBridge.Msg("toast", "text", "Settings saved", "kind", "ok"));
                await PushStateAsync();
            });

            _bridge.Register("vm.clone", async message =>
            {
                object result = await CallService("vm.clone", Json.Obj(
                    "id", Json.GetString(message, "id", ""),
                    "name", Json.GetString(message, "name", "")), 600000);

                Dictionary<string, object> view = result as Dictionary<string, object>;
                _bridge.Send(WebBridge.Msg("toast", "text",
                    "Cloned as " + Json.GetString(view, "name", ""), "kind", "ok"));

                await PushStateAsync();
            });

            _bridge.Register("vm.export", async message =>
            {
                string id = Json.GetString(message, "id", "");
                string name = Json.GetString(message, "name", id);

                string path = AskSave("Export virtual machine",
                    SafeFileName(name) + VmArchive.Extension,
                    "hv86 VM archive (*" + VmArchive.Extension + ")|*" + VmArchive.Extension);

                if (string.IsNullOrEmpty(path)) return;

                object result = await CallService("vm.export",
                    Json.Obj("id", id, "path", path), 1800000);

                Dictionary<string, object> map = result as Dictionary<string, object>;
                long size = Json.GetLong(map, "size", 0);

                _bridge.Send(WebBridge.Msg("toast", "text",
                    "Exported: " + Json.GetString(map, "path", path) +
                    " (" + Math.Round(size / 1024.0 / 1024.0, 1) + " MB)", "kind", "ok"));

                Reveal(Json.GetString(map, "path", path));
            });

            _bridge.Register("vm.import", async delegate
            {
                string path = AskOpen("Import virtual machine",
                    "hv86 VM archive (*" + VmArchive.Extension + ")|*" + VmArchive.Extension +
                    "|All files (*.*)|*.*");

                if (string.IsNullOrEmpty(path)) return;

                object result = await CallService("vm.import", Json.Obj("path", path), 1800000);
                Dictionary<string, object> view = result as Dictionary<string, object>;

                _bridge.Send(WebBridge.Msg("toast", "text",
                    "Imported: " + Json.GetString(view, "name", ""), "kind", "ok"));

                await PushStateAsync();
            });

            _bridge.Register("host.diagnostics", async delegate
            {
                object result = await CallService("host.diagnostics", null, 300000);
                Dictionary<string, object> map = result as Dictionary<string, object>;

                string path = Json.GetString(map, "path", "");
                _bridge.Send(WebBridge.Msg("toast", "text", "Diagnostics: " + path, "kind", "ok"));

                Reveal(path);
            });
            _bridge.Register("open.vm", message =>
            {
                string id = Json.GetString(message, "id", "");
                if (VmStore.IsValidId(id)) OpenFolder(Paths.VmDir(id));
            });

            _bridge.Register("host.log", async delegate
            {
                try
                {
                    object result = await CallService("host.log", Json.Obj("lines", 400), 15000);
                    Dictionary<string, object> map = result as Dictionary<string, object>;
                    _bridge.Send(WebBridge.Msg("log", "text", Json.GetString(map, "text", "")));
                }
                catch (Exception ex)
                {
                    _bridge.Send(WebBridge.Msg("log", "text", ex.Message));
                }
            });
        }

        private async Task ServiceAction(string action)
        {
            ServiceResult result;

            if (action == "install")
            {
                _bridge.Send(WebBridge.Msg("toast", "text", "Installing service...", "kind", "info"));
                result = await Task.Run(() => ServiceManager.Install(true));
            }
            else if (action == "start") result = await Task.Run(() => ServiceManager.Start());
            else if (action == "stop") result = await Task.Run(() => ServiceManager.Stop());
            else result = await Task.Run(() => ServiceManager.Uninstall());

            _bridge.Send(WebBridge.Msg("toast", "text", result.Message, "kind", result.Ok ? "ok" : "bad"));

            if (action == "install" || action == "start")
                await Task.Delay(600);

            await PushStateAsync();
        }

        // ------------------------------------------------------------- service io

        private async Task<object> CallService(string method, Dictionary<string, object> parameters, int timeoutMs)
        {
            await EnsureClientAsync();

            PipeClient client = _client;
            if (client == null || !client.IsConnected) throw new Hv86Exception("service is not running");

            return await client.CallForResultAsync(method, parameters, timeoutMs).ConfigureAwait(false);
        }

        private async Task EnsureClientAsync()
        {
            if (_client != null && _client.IsConnected) return;

            await _clientGate.WaitAsync().ConfigureAwait(false);

            try
            {
                if (_client != null && _client.IsConnected) return;

                try { _client?.Dispose(); } catch { }

                PipeClient client = new PipeClient(PipeNames.Service);
                client.EventReceived += OnServiceEvent;

                if (!await client.ConnectAsync(700).ConfigureAwait(false))
                {
                    client.Dispose();
                    _client = null;
                    return;
                }

                _client = client;
                Log.Info("UI", "подключился к службе");
            }
            finally
            {
                _clientGate.Release();
            }
        }

        private void OnServiceEvent(string name, Dictionary<string, object> data)
        {
            _bridge.Send(WebBridge.Msg("event", "name", name, "data", data));
        }

        private void PushState()
        {
            Task.Run(async () =>
            {
                try { await PushStateAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log.Warn("UI", "состояние: " + ex.Message); }
            });
        }

        private async Task PushStateAsync()
        {
            ServiceStatus status = await Task.Run(() => ServiceManager.Query()).ConfigureAwait(false);

            Dictionary<string, object> payload = WebBridge.Msg("state",
                "service", Json.Obj(
                    "installed", status.Installed,
                    "running", status.Running,
                    "state", status.State,
                    "problem", status.Problem),
                "elevated", Elevation.IsElevated,
                "version", AppInfo.Version,
                "v86", AppInfo.V86Version,
                "data_dir", Paths.Root);

            object host = null;
            object vms = new List<object>();
            object presets = new List<object>();

            try
            {
                host = await CallService("host.status", null, 5000).ConfigureAwait(false);
                vms = await CallService("vm.list", null, 5000).ConfigureAwait(false);
                presets = await CallService("vm.presets", null, 5000).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                payload["service_error"] = ex.Message;

                try { presets = PresetsOffline(); }
                catch { }
            }

            payload["host"] = host;
            payload["vms"] = vms ?? new List<object>();
            payload["presets"] = presets ?? new List<object>();

            _bridge.Send(payload);
        }

        /// <summary>Пресеты без службы: их каталог вшит в exe.</summary>
        private static object PresetsOffline()
        {
            List<object> list = new List<object>();

            foreach (Dictionary<string, object> preset in Vm.PresetCatalog.All())
            {
                list.Add(Json.Obj(
                    "id", Json.GetString(preset, "id", ""),
                    "name", Json.GetString(preset, "name", ""),
                    "description", Json.GetString(preset, "description", ""),
                    "kind", Json.GetString(preset, "kind", ""),
                    "memory_mb", Json.GetInt(preset, "memory_mb", 128),
                    "disk_mb", Json.GetInt(preset, "disk_mb", 0),
                    "size", Json.GetLong(preset, "size", 0)));
            }

            return list;
        }

        private async Task SendSnapshotsAsync(string id)
        {
            try
            {
                object items = await CallService("vm.snapshots", Json.Obj("id", id), 30000).ConfigureAwait(false);
                _bridge.Send(WebBridge.Msg("snapshots", "id", id, "items", items));
            }
            catch (Exception ex)
            {
                _bridge.Send(WebBridge.Msg("snapshots", "id", id, "items", new List<object>(),
                    "error", ex.Message));
            }
        }

        // ------------------------------------------------------------- helpers

        private void OpenHeadlessConsole(string id, string name)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, string>(OpenHeadlessConsole), id, name);
                return;
            }

            HeadlessConsoleWindow window;

            if (_headlessWindows.TryGetValue(id, out window) && window != null && !window.IsDisposed)
            {
                window.Activate();
                window.BringToFront();
                return;
            }

            window = new HeadlessConsoleWindow(id, name);
            window.FormClosed += delegate { _headlessWindows.Remove(id); };

            _headlessWindows[id] = window;
            window.Show();
            window.Activate();
        }

        private static Dictionary<string, object> Clone(Dictionary<string, object> source)
        {
            Dictionary<string, object> copy = new Dictionary<string, object>(StringComparer.Ordinal);
            if (source == null) return copy;

            foreach (KeyValuePair<string, object> pair in source) copy[pair.Key] = pair.Value;
            return copy;
        }

        private string AskFile(string title, string filter)
        {
            if (InvokeRequired)
                return (string)Invoke(new Func<string, string, string>(AskFile), title, filter);

            using (OpenFileDialog dialog = new OpenFileDialog
            {
                Title = title,
                Filter = filter,
                CheckFileExists = true,
                Multiselect = false
            })
            {
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
            }
        }

        private string AskSave(string title, string suggested, string filter)
        {
            if (InvokeRequired)
                return (string)Invoke(new Func<string, string, string, string>(AskSave), title, suggested, filter);

            using (SaveFileDialog dialog = new SaveFileDialog
            {
                Title = title,
                Filter = filter,
                FileName = suggested,
                OverwritePrompt = true,
                AddExtension = true
            })
            {
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
            }
        }

        private string AskOpen(string title, string filter)
        {
            if (InvokeRequired)
                return (string)Invoke(new Func<string, string, string>(AskOpen), title, filter);

            using (OpenFileDialog dialog = new OpenFileDialog
            {
                Title = title,
                Filter = filter,
                CheckFileExists = true
            })
            {
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.FileName : null;
            }
        }

        private string AskFolder(string title)
        {
            if (InvokeRequired)
                return (string)Invoke(new Func<string, string>(AskFolder), title);

            using (FolderBrowserDialog dialog = new FolderBrowserDialog
            {
                Description = title,
                ShowNewFolderButton = true
            })
            {
                return dialog.ShowDialog(this) == DialogResult.OK ? dialog.SelectedPath : null;
            }
        }

        private static string SafeFileName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "vm";

            string result = name.Trim();
            foreach (char bad in Path.GetInvalidFileNameChars()) result = result.Replace(bad, '_');
            return result;
        }

        private static void Reveal(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;

                if (File.Exists(path))
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe",
                        "/select,\"" + path + "\"") { UseShellExecute = true });
                else
                    OpenFolder(Path.GetDirectoryName(path));
            }
            catch (Exception ex)
            {
                Log.Warn("UI", "показ файла: " + ex.Message);
            }
        }

        private static void OpenFolder(string path)
        {
            try
            {
                if (string.IsNullOrEmpty(path)) return;
                Directory.CreateDirectory(path);
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Log.Warn("UI", "открытие каталога: " + ex.Message);
            }
        }

        private static void OpenExternal(string url)
        {
            if (string.IsNullOrEmpty(url)) return;
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn("UI", "внешняя ссылка: " + ex.Message); }
        }

        // ------------------------------------------------------------- messaging

        private void PostToPage(Dictionary<string, object> payload)
        {
            string json = Json.Stringify(payload);

            Action deliver = delegate
            {
                try
                {
                    CoreWebView2 core = _web == null ? null : _web.CoreWebView2;
                    if (core != null && _webReady) core.PostWebMessageAsJson(json);
                }
                catch (Exception ex) { Log.Warn("UI", "post: " + ex.Message); }
            };

            if (IsHandleCreated && InvokeRequired) BeginInvoke(deliver);
            else deliver();
        }

        // ------------------------------------------------------------- visibility

        public void ShowWindow()
        {
            if (InvokeRequired) { BeginInvoke(new Action(ShowWindow)); return; }

            ShowInTaskbar = true;
            if (!Visible) Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
            BringToFront();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (_reallyClosing) return;

            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                ShowInTaskbar = false;

                if (!_trayHintShown)
                {
                    _trayHintShown = true;
                    _tray.ShowBalloonTip(2000, AppInfo.ProductName,
                        "hv86 продолжает работать в трее. ВМ не останавливаются.", ToolTipIcon.Info);
                }

                return;
            }

            _reallyClosing = true;
        }

        public void CloseForReal()
        {
            if (InvokeRequired) { BeginInvoke(new Action(CloseForReal)); return; }

            _reallyClosing = true;
            Close();
        }
    }
}