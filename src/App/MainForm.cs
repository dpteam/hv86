using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
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
        /// <summary>
        /// «Origin» страницы менеджера. Именно origin, а не NavigateToString:
        /// у about:blank он непрозрачный, и localStorage бросает SecurityError,
        /// от которого умирал весь скрипт (кнопки переставали работать).
        /// </summary>
        private const string Origin = "http://hv86.local";

        private readonly WebView2 _web;
        private readonly WebBridge _bridge;
        private readonly System.Windows.Forms.Timer _timer;
        private readonly NotifyIcon _tray;
        private readonly SemaphoreSlim _clientGate = new SemaphoreSlim(1, 1);
        private readonly Dictionary<string, HeadlessConsoleWindow> _headlessWindows =
            new Dictionary<string, HeadlessConsoleWindow>(StringComparer.Ordinal);

        private PipeClient _client;
        private ServiceRunner _engine;          // портативный движок: службы нет - держим движок сами
        private bool _engineTried;
        private bool _webReady;
        private bool _pageReady;
        private bool _reallyClosing;

        /// <summary>
        /// Последнее известное число работающих ВМ. Обновляется в PushState,
        /// чтобы обработчик закрытия не ходил в службу из UI-потока.
        /// </summary>
        private volatile int _vmsRunning;

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
            menu.Items.Add("Свернуть в трей", null, delegate
            {
                Hide();
                ShowInTaskbar = false;
            });
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
                try { _engine?.Stop(); } catch { }
                _engine = null;
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
                        !e.Uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase) &&
                        !e.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) &&
                        !e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    {
                        e.Cancel = true;
                        OpenExternal(e.Uri);
                    }
                };

                // страница отдаётся своим обработчиком, как у консоли: origin
                // нужен настоящий (см. Origin), да и devtools так понятнее
                core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += OnManagerRequest;

                core.WebMessageReceived += delegate (object s, CoreWebView2WebMessageReceivedEventArgs e)
                {
                    string json;
                    try { json = e.WebMessageAsJson; }
                    catch (Exception ex) { Log.Warn("UI", "webmessage: " + ex.Message); return; }

                    _bridge.Handle(json);
                };

                core.Navigate(Origin + "/index.html");
                _webReady = true;

                Log.Info("UI", "webview2 " + core.Environment.BrowserVersionString + " готов");

                // если страница так и не сказала ready - интерфейс мёртв, и молчать об
                // этом нельзя: кнопки просто ничего не делают
                _ = Task.Delay(10000).ContinueWith(delegate
                {
                    if (_pageReady) return;

                    Log.Error("UI", "страница не прислала ready за 10 с: интерфейс не работает " +
                                    "(ошибки страницы должны быть строкой выше в этом же логе)");
                });

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

        /// <summary>
        /// Отдаёт страницу менеджера по http://hv86.local/. Всё в ней вшито
        /// (css/js/i18n), поэтому маршрут один - остальное 404.
        /// </summary>
        private void OnManagerRequest(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                string path = e.Request.Uri ?? "";
                int query = path.IndexOf('?');
                if (query >= 0) path = path.Substring(0, query);

                bool page = path.EndsWith("/index.html", StringComparison.OrdinalIgnoreCase) ||
                            path.Equals(Origin + "/", StringComparison.OrdinalIgnoreCase);

                if (!page)
                {
                    e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(
                        null, 404, "Not Found", "Content-Length: 0");
                    return;
                }

                byte[] bytes = Encoding.UTF8.GetBytes(BuildHtml());

                e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(bytes, false), 200, "OK",
                    "Content-Type: text/html; charset=utf-8\r\n" +
                    "Content-Length: " + bytes.Length + "\r\n" +
                    "Cache-Control: no-store");
            }
            catch (Exception ex)
            {
                Log.Exception("UI", "отдача страницы", ex);
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
            _bridge.Register("ready", delegate
            {
                _pageReady = true;
                PushState();
                _ = SendFontsAsync();
            });

            _bridge.Register("refresh", delegate { PushState(); });

            // ошибки самой страницы (window.onerror в manager.html): иначе
            // сломанный интерфейс выглядит как "кнопки не работают" и молчит
            _bridge.Register("error", delegate (Dictionary<string, object> message)
            {
                Log.Error("UI", "страница: " + Json.GetString(message, "message", ""));
            });

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

            _bridge.Register("vm.promote-replica", async message =>
            {
                string replica = Json.GetString(message, "replica", "");

                object result = await CallService("vm.promote-replica",
                    Json.Obj("replica", replica), 600000);

                Dictionary<string, object> view = result as Dictionary<string, object>;

                _bridge.Send(WebBridge.Msg("toast", "text",
                    "Promoted " + Json.GetString(view, "name", replica) + " from the replica",
                    "kind", "ok"));

                await PushStateAsync();
            });

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
                string pipe = Json.GetString(view, "pipe", "");

                // «Подключиться» больше не запускает ВМ: как в Hyper-V, окно
                // можно открыть и для выключенной, а старт - отдельной кнопкой
                if (engine == "headless")
                {
                    string font = Json.GetString(view, "text_font", "");
                    int fontSize = Json.GetInt(view, "text_font_size", 0);

                    if (string.IsNullOrEmpty(font)) font = HostSettings.TextFont;
                    if (fontSize <= 0) fontSize = HostSettings.TextFontSize;

                    OpenHeadlessConsole(id, name, pipe, font, fontSize);
                }
                else
                {
                    await CallService("vm.console", Json.Obj("id", id, "start", false), 120000);
                }

                await PushStateAsync();
            });

            _bridge.Register("open.data", delegate { OpenFolder(Paths.Root); });
            _bridge.Register("open.logs", delegate { OpenFolder(Paths.LogDir); });

            // настройки хоста: сейчас только шрифт текстового режима
            _bridge.Register("host.settings.set", async message =>
            {
                Dictionary<string, object> parameters = Clone(message);
                parameters.Remove("action");

                try
                {
                    await CallService("host.settings.set", parameters, 15000).ConfigureAwait(false);
                    HostSettings.Invalidate();
                }
                catch (Exception ex)
                {
                    Log.Warn("UI", "настройки хоста: " + ex.Message);
                }

                await PushStateAsync().ConfigureAwait(false);
            });

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

            // перенос ВМ на встроенный движок: диски остаются, снапшоты
            // старой версии уходят - UI спрашивает подтверждение заранее
            _bridge.Register("vm.migrate", async message =>
            {
                object result = await CallService("vm.migrate", Json.Obj(
                    "id", Json.GetString(message, "id", "")), 300000);

                Dictionary<string, object> summary = result as Dictionary<string, object>;

                if (Json.GetBool(summary, "migrated", false))
                    _bridge.Send(WebBridge.Msg("toast", "text",
                        "Moved to v86 " + Json.GetString(summary, "to", "") +
                        ": snapshots removed " + Json.GetInt(summary, "snapshots", 0),
                        "kind", "ok"));
                else
                    _bridge.Send(WebBridge.Msg("toast", "text",
                        "Already on v86 " + Json.GetString(summary, "version", ""), "kind", "info"));

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

            _bridge.Register("host.fonts.pick", delegate
            {
                string path = AskFile("Load a font for the text mode",
                    "Fonts (*.ttf;*.otf;*.ttc)|*.ttf;*.otf;*.ttc|All files (*.*)|*.*");

                if (string.IsNullOrEmpty(path)) return;

                Task.Run(async delegate
                {
                    try
                    {
                        await CallService("host.fonts.import", Json.Obj("path", path), 30000);
                        await SendFontsAsync();
                    }
                    catch (Exception ex)
                    {
                        _bridge.Send(WebBridge.Msg("error", "message", ex.Message));
                    }
                });
            });

            _bridge.Register("host.fonts.remove", async message =>
            {
                await CallService("host.fonts.remove", Json.Obj(
                    "file", Json.GetString(message, "file", "")), 15000);

                await SendFontsAsync();
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

        /// <summary>
        /// Портативный режим: службы нет — поднимаем движок в этом же процессе.
        ///
        /// Зачем: после распаковки архива и двойного клика службы ещё нет, а без
        /// движка интерфейсу некуда отправлять команды — все кнопки просто
        /// ничего не делают, и это выглядит как «сломалось». Движок тот же
        /// самый (`ServiceRunner`), что у `--engine` и у службы: канал, ВМ и
        /// сторож те же, разница только в том, кто его держит.
        ///
        /// Цена: ВМ живут, пока открыт интерфейс (vmhost — отдельные процессы,
        /// но движок в этом). Для «пусть работает в фоне» нужна служба.
        /// </summary>
        private void EnsurePortableEngine(ServiceStatus status)
        {
            if (_engine != null || _engineTried) return;

            // служба установлена — не отбираем у неё канал, даже если она стоит:
            // её и надо запускать, а не плодить второй движок
            if (status.Installed) return;

            _engineTried = true;

            try
            {
                ServiceRunner engine = new ServiceRunner();
                engine.Start();

                _engine = engine;
                Log.Info("UI", "портативный режим: движок работает в процессе интерфейса (служба не установлена)");
            }
            catch (Exception ex)
            {
                Log.Exception("UI", "портативный движок", ex);
            }
        }

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

            // службы нет вообще (свежий архив, двойной клик) - иначе интерфейсу
            // некуда отправлять команды и все кнопки "мертвы"
            EnsurePortableEngine(status);

            Dictionary<string, object> payload = WebBridge.Msg("state",
                "service", Json.Obj(
                    "installed", status.Installed,
                    "running", status.Running,
                    "state", status.State,
                    "problem", status.Problem),
                "portable", _engine != null,
                "host_settings", HostSettings.View(),
                "elevated", Elevation.IsElevated,
                "version", AppInfo.Version,
                "v86", AppInfo.V86Version,
                "data_dir", Paths.Root);

            object host = null;
            object vms = new List<object>();
            object presets = new List<object>();
            object replicas = new List<object>();

            try
            {
                host = await CallService("host.status", null, 5000).ConfigureAwait(false);
                vms = await CallService("vm.list", null, 5000).ConfigureAwait(false);
                presets = await CallService("vm.presets", null, 5000).ConfigureAwait(false);

                // принятые реплики: на основном хосте список пуст, на реплике -
                // это то, что можно поднять одной кнопкой
                replicas = await CallService("host.replicas", null, 5000).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                payload["service_error"] = ex.Message;

                try { presets = PresetsOffline(); }
                catch { }
            }

            // кэш для обработчика закрытия: там нельзя ждать службу
            Dictionary<string, object> statusView = host as Dictionary<string, object>;
            _vmsRunning = statusView == null ? 0 : Json.GetInt(statusView, "vms_running", 0);

            payload["host"] = host;
            payload["vms"] = vms ?? new List<object>();
            payload["presets"] = presets ?? new List<object>();
            payload["replicas"] = replicas ?? new List<object>();

            _bridge.Send(payload);
        }

        /// <summary>Отправляет странице список шрифтов (системные + загруженные).</summary>
        private async Task SendFontsAsync()
        {
            object fonts = null;

            try
            {
                fonts = await CallService("host.fonts", null, 15000).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("FONT", "список шрифтов: " + ex.Message);

                // без службы показываем хотя бы системные
                fonts = Json.Obj(
                    "system", FontCatalog.SystemFonts(),
                    "custom", FontCatalog.Custom(),
                    "all", FontCatalog.All());
            }

            _bridge.Send(WebBridge.Msg("fonts", "fonts", fonts));
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

        private void OpenHeadlessConsole(string id, string name, string pipeName,
            string fontFamily = null, int fontSize = 0)
        {
            if (InvokeRequired)
            {
                BeginInvoke(new Action<string, string, string, string, int>(OpenHeadlessConsole),
                    id, name, pipeName, fontFamily, fontSize);
                return;
            }

            HeadlessConsoleWindow window;

            if (_headlessWindows.TryGetValue(id, out window) && window != null && !window.IsDisposed)
            {
                window.Activate();
                window.BringToFront();
                return;
            }

            window = new HeadlessConsoleWindow(id, name, pipeName, fontFamily, fontSize);
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

            // Крестик закрывает приложение. Подтверждение нужно только в
            // портативном режиме (движок живёт в этом процессе) и только если
            // ВМ реально работают.
            //
            // Раньше здесь был синхронный запрос к службе прямо в UI-потоке
            // (connect до 700 мс + запрос до 2 с). Если служба была занята,
            // окно на это время замирало, а вопрос всплывал уже после второго
            // клика - со стороны это выглядело как «не закрывается». Теперь
            // берём последнее известное значение из PushState: закрытие не
            // ждёт сеть вообще.
            int running = _vmsRunning;
            bool portable = _engine != null;

            Log.Info("UI", "закрытие окна: портативный=" + portable + ", ВМ работает=" + running +
                           ", причина=" + e.CloseReason);

            if (e.CloseReason == CloseReason.UserClosing && portable && running > 0)
            {
                // окно могло быть позади других - поднимаем, иначе вопрос
                // останется незамеченным
                try { Activate(); BringToFront(); } catch { }

                DialogResult answer = MessageBox.Show(this,
                    "ВМ работают внутри этого окна (портативный режим без службы).\r\n" +
                    "Закрыть hv86 и остановить их? Диски сохранятся.",
                    AppInfo.ProductName, MessageBoxButtons.OKCancel, MessageBoxIcon.Question,
                    MessageBoxDefaultButton.Button2);

                Log.Info("UI", "подтверждение закрытия: " + answer);

                if (answer != DialogResult.OK)
                {
                    e.Cancel = true;
                    return;
                }
            }

            _reallyClosing = true;
        }

        /// <summary>
        /// Сколько ВМ работает, по последнему ответу службы. Никаких запросов:
        /// вызывается из обработчика закрытия, где блокировать UI нельзя.
        /// </summary>
        public int RunningVms()
        {
            return _vmsRunning;
        }

        public void CloseForReal()
        {
            if (InvokeRequired) { BeginInvoke(new Action(CloseForReal)); return; }

            _reallyClosing = true;
            Close();
        }
    }
}