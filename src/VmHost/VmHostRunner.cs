using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Hv86.Core;
using Hv86.Vm;

namespace Hv86.VmHost
{
    /// <summary>
    /// Процесс одной ВМ: окно с v86, канал управления для службы и весь
    /// обмен «страница ↔ диск».
    ///
    /// Здесь же живут две вещи, которые нельзя делать в UI-потоке:
    ///   * приём state-файла кусками (base64 через WebView2) и его запись;
    ///   * сохранение диска: после save_state мы применяем «грязные» блоки
    ///     из state к образу на хосте (см. V86State) - так изменения гостя
    ///     не теряются при штатной остановке, без патчей v86.
    /// </summary>
    public sealed class VmHostRunner
    {
        private const int StateChunkSize = 512 * 1024;

        /// <summary>Сколько символов вставки принимаем: больше - это уже не текст, а файл.</summary>
        private const int MaxPasteChars = 4096;

        /// <summary>Сообщение из страницы, которое обрабатывалось дольше этого, попадает в лог.</summary>
        private const long SlowMessageMs = 250;

        private readonly string _vmId;
        private readonly bool _showConsole;
        private readonly string _restorePath;
        private readonly bool _smoke;
        private readonly bool _replica;
        private readonly int _replicaRelayPort;
        private bool _idle;

        private VmDefinition _vm;
        private VmHostForm _form;
        private PipeServer _server;
        private PortForwarder _forwarder;
        private WispRelay _relay;
        private EgressGate _egress;

        private string _state = "starting";
        private string _error = "";
        private long _instructions;
        private int _mips;
        private bool _shuttingDown;
        private int _exitCode;

        private readonly object _gate = new object();
        private readonly Dictionary<string, PendingSave> _saves =
            new Dictionary<string, PendingSave>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _stateFiles =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        private Dictionary<string, object> _bootConfig;
        private TaskCompletionSource<Dictionary<string, object>> _xhrProbe;
        private TaskCompletionSource<bool> _restoreWait;
        private TaskCompletionSource<bool> _typeWait;
        private TaskCompletionSource<Dictionary<string, object>> _fetchProbe;
        private TaskCompletionSource<Dictionary<string, object>> _scaleProbe;
        private bool _promoted;

        private sealed class PendingSave
        {
            public string Token;
            public string Path;
            public FileStream Stream;
            public int NextSequence;
            public long BytesWritten;
            public TaskCompletionSource<Dictionary<string, object>> Completion =
                new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public VmHostRunner(string vmId, bool showConsole, string restorePath, bool smoke, bool replica = false,
            int relayPort = 0, bool idle = false)
        {
            _vmId = vmId;
            _showConsole = showConsole;
            _restorePath = restorePath;
            _smoke = smoke;
            _replica = replica;
            _replicaRelayPort = relayPort;
            _idle = idle;
        }

        // ------------------------------------------------------------------ run

        public int Run()
        {
            _vm = VmStore.Load(_vmId);

            if (_vm == null)
            {
                Log.Error("VMHOST", "ВМ не найдена: " + _vmId);
                return 2;
            }

            Paths.EnsureVmDirectories(_vm.Id);

            if (_vm.V86Version != AppInfo.V86Version)
            {
                Log.Warn("VMHOST", "ВМ сделана в v86 " + _vm.V86Version + ", встроена " +
                                   AppInfo.V86Version + ": state-снапшоты могут не подойти");
            }

            if (!string.IsNullOrEmpty(_restorePath))
            {
                if (File.Exists(_restorePath))
                {
                    _stateFiles["restore.bin"] = _restorePath;
                    Log.Info("VMHOST", "восстановление из " + _restorePath);
                }
                else
                {
                    Log.Warn("VMHOST", "state-файл не найден: " + _restorePath);
                }
            }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            _form = new VmHostForm(_vm.Id, _vm.Name, _replica);

            if (!_showConsole) _form.PlaceOffScreen();

            _form.PageMessage += OnPageMessage;
            _form.Shown += async delegate { await StartAsync().ConfigureAwait(true); };

            Application.Run(_form);

            Log.Info("VMHOST", "процесс завершён, код " + _exitCode);
            return _exitCode;
        }

        private async Task StartAsync()
        {
            try
            {
                if (!await _form.InitAsync(BuildConsoleHtml()).ConfigureAwait(true))
                {
                    _exitCode = 3;
                    _form.CloseForReal();
                    return;
                }

                _form.ResourceServer.MediaPath = ResolveMedia;
                _form.ResourceServer.StatePath = ResolveState;

                // исходящий трафик fetch-бэкенда (net.mode=nat): страница
                // спрашивает разрешение на каждый не-свой запрос, решаем здесь
                _egress = new EgressGate(BuildEgressPolicy(_vm));
                _form.ResourceServer.Egress = _egress;
                Log.Info("NET", _egress.Policy.Describe());

                // общая папка: файлы трогаем только в C#, страница просит операции
                if (!string.IsNullOrEmpty(_vm.SharePath))
                {
                    try
                    {
                        _form.ResourceServer.Share = new ShareFileServer(_vm.SharePath, _vm.ShareReadOnly);
                        Log.Info("NET", "share: " + _vm.SharePath + (_vm.ShareReadOnly ? " (ro)" : " (rw)"));
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("NET", "share недоступна: " + ex.Message);
                    }
                }

                _server = new PipeServer(_replica ? PipeNames.VmReplica(_vm.Id) : PipeNames.VmHost(_vm.Id),
                    DispatchAsync);
                _server.Start();

                _forwarder = new PortForwarder(message => _form.Post(message));
                _forwarder.Report += text => Broadcast("vm.forward", Json.Obj("id", _vmId, "text", text));

                // реплика молчит: порты и релей занимает основная ВМ. Свой
                // релей реплика поднимет на её же порту уже после повышения
                if (!_replica)
                {
                    // проброс портов работает только на NAT-стеке fetch: в режиме
                    // relay внутрь гостя подключиться нечем
                    _forwarder.Apply(_vm.NetMode == "nat" ? _vm.Forwards : new List<ForwardRule>());

                    // полный доступ в сеть: wisp-релей поднимаем только если он
                    // включён, иначе остаётся fetch (HTTP + проброс портов)
                    if (_vm.NetMode == "relay") StartRelay(0);
                }

                // ВМ выключена, но окно открыто: гостя не создаём, пока служба не попросит
                // (vm.boot). Так "Подключиться" не запускает ВМ сама.
                if (_idle)
                {
                    _state = "idle";
                    _form.Post(Json.Obj("action", "idle", "id", _vm.Id, "name", _vm.Name));
                    Log.Info("VMHOST", "окно открыто без гостя (ВМ выключена)");
                }
                else
                {
                    _bootConfig = BuildBootConfig();
                }

                if (!_showConsole) _form.HideConsole();

                if (_smoke) _ = Task.Run(() => RunSmokeAsync());
            }
            catch (Exception ex)
            {
                Log.Exception("VMHOST", "старт", ex);
                _exitCode = 4;
                _form.CloseForReal();
            }
        }

        // -------------------------------------------------------------- страница

        private void OnPageMessage(Dictionary<string, object> message)
        {
            string action = Json.GetString(message, "action", "");

            // Сообщения из страницы приходят в UI-поток: если обработчик
            // задумался (например, пишет state на диск), окно встаёт. Пишем
            // такие случаи в лог, чтобы не угадывать по симптомам.
            Stopwatch watch = Stopwatch.StartNew();

            try
            {
                HandlePageMessage(action, message);
            }
            finally
            {
                if (watch.ElapsedMilliseconds > SlowMessageMs)
                    Log.Warn("CON", "медленное сообщение " + watch.ElapsedMilliseconds + " мс: " + action);
            }
        }

        private void HandlePageMessage(string action, Dictionary<string, object> message)
        {
            switch (action)
            {
                case "ready":
                    Log.Info("VMHOST", "страница консоли готова");
                    if (_bootConfig != null) SendBoot();
                    else if (_idle) _form.Post(Json.Obj("action", "idle", "id", _vm.Id, "name", _vm.Name));
                    break;

                case "log":
                    Log.Info("CON", Json.GetString(message, "text", ""));
                    break;

                case "state":
                    _state = Json.GetString(message, "state", _state);
                    Broadcast("vm.state", Json.Obj("id", _vmId, "state", _state, "error", _error));
                    break;

                case "metrics":
                    _instructions = Json.GetLong(message, "instructions", _instructions);
                    _mips = Json.GetInt(message, "mips", _mips);

                    Broadcast("vm.metrics", Json.Obj(
                        "id", _vmId,
                        "instructions", _instructions,
                        "mips", _mips,
                        "relay_port", _relay == null ? 0 : _relay.Port,
                        "relay_clients", _relay == null ? 0 : _relay.Handshakes,
                        "state", _state));
                    break;

                case "error":
                    _error = Json.GetString(message, "message", "");
                    Log.Error("CON", _error);
                    Broadcast("vm.error", Json.Obj("id", _vmId, "message", _error));
                    break;

                case "toast":
                    Broadcast("vm.toast", Json.Obj("id", _vmId,
                        "text", Json.GetString(message, "text", ""),
                        "kind", Json.GetString(message, "kind", "info")));
                    break;

                case "ctrl":
                    HandleControl(Json.GetString(message, "cmd", ""));
                    break;

                case "stateChunk":
                    OnStateChunk(message);
                    break;

                case "stateDone":
                    OnStateDone(message);
                    break;

                case "stateError":
                    OnStateError(message);
                    break;

                case "screenshot":
                    SaveScreenshot(Json.GetString(message, "data", ""));
                    break;

                case "tcpData":
                    _forwarder?.GuestData(Json.GetString(message, "session", ""),
                        Json.GetString(message, "data", ""));
                    break;

                case "tcpOpened":
                    _forwarder?.GuestOpened(Json.GetString(message, "session", ""));
                    break;

                case "tcpClosed":
                    _forwarder?.GuestClosed(Json.GetString(message, "session", ""),
                        Json.GetString(message, "error", ""));
                    break;

                case "xhrResult":
                    _xhrProbe?.TrySetResult(message);
                    break;

                case "restoreDone":
                    _restoreWait?.TrySetResult(Json.GetBool(message, "ok", false));
                    if (!Json.GetBool(message, "ok", false))
                        _error = Json.GetString(message, "error", "restore failed");
                    break;

                // Ctrl+V в консоли: страница не имеет доступа к буферу обмена
                // хоста, поэтому текст читает процесс ВМ (он в сессии пользователя)
                case "paste":
                    PasteFromHost();
                    break;

                case "typed":
                    _typeWait?.TrySetResult(Json.GetBool(message, "ok", true));
                    if (!Json.GetBool(message, "ok", true))
                        _error = Json.GetString(message, "error", "typing failed");
                    break;

                case "fetchResult":
                    _fetchProbe?.TrySetResult(message);
                    break;

                case "scaleResult":
                    _scaleProbe?.TrySetResult(message);
                    break;
            }
        }

        private void SendBoot()
        {
            Log.Info("VMHOST", "запуск эмулятора");
            _form.Post(Json.Obj("action", "boot", "config", _bootConfig));
        }

        private void HandleControl(string command)
        {
            switch (command)
            {
                case "hide":
                    _form.HideConsole();
                    break;

                case "iso":
                    PickAndAttach(-1);
                    break;

                // кнопка Start на экране "выключена": запускать ВМ должен
                // служба-владелец (состояние, диски, чекпоинты), а не окно
                case "start":
                    StartViaService();
                    break;

                case "eject":
                    Task.Run(() => DetachAsync("cdrom"));
                    break;

                case "snapshot":
                    Task.Run(async () =>
                    {
                        try
                        {
                            string name = "manual-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
                            await SnapshotToVmFolderAsync(name).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Log.Error("SNAP", ex.Message);
                            Broadcast("vm.toast", Json.Obj("id", _vmId, "text", ex.Message, "kind", "bad"));
                        }
                    });
                    break;
            }
        }

        /// <summary>
        /// Просит службу запустить эту ВМ (кнопка Start на экране «выключена»).
        /// Служба увидит, что окно уже открыто (`runtime.Idle`), и пришлёт сюда
        /// `vm.boot` - новый процесс не поднимается, канал остаётся этот.
        /// </summary>
        private void StartViaService()
        {
            Task.Run(async delegate
            {
                try
                {
                    using (PipeClient client = new PipeClient(PipeNames.Service))
                    {
                        if (!await client.ConnectAsync(5000).ConfigureAwait(false))
                        {
                            Broadcast("vm.error", Json.Obj("id", _vmId, "message", "служба не отвечает"));
                            return;
                        }

                        await client.CallForResultAsync("vm.start", Json.Obj("id", _vmId), 180000)
                            .ConfigureAwait(false);
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("VMHOST", "запуск через службу: " + ex.Message);
                    Broadcast("vm.error", Json.Obj("id", _vmId, "message", ex.Message));
                }
            });
        }

        private void PickAndAttach(object _)
        {
            _form.BeginInvoke(new Action(async delegate
            {
                using (OpenFileDialog dialog = new OpenFileDialog
                {
                    Title = "Attach ISO / disk image",
                    Filter = "Disk images (*.iso;*.img;*.bin;*.raw)|*.iso;*.img;*.bin;*.raw|All files (*.*)|*.*",
                    CheckFileExists = true
                })
                {
                    if (dialog.ShowDialog(_form) != DialogResult.OK) return;

                    try { await AttachAsync(dialog.FileName, "cdrom").ConfigureAwait(true); }
                    catch (Exception ex)
                    {
                        Log.Error("MEDIA", ex.Message);
                        Broadcast("vm.toast", Json.Obj("id", _vmId, "text", ex.Message, "kind", "bad"));
                    }
                }
            }));
        }

        // --------------------------------------------------------------- boot

        private Dictionary<string, object> BuildBootConfig()
        {
            Dictionary<string, object> config = Json.Obj(
                "id", _vm.Id,
                "name", _vm.Name,
                "memory", (long)_vm.MemoryMb * 1024 * 1024,
                "vgaMemory", (long)_vm.VgaMemoryMb * 1024 * 1024,
                "bootOrder", BootOrderValue(_vm.BootOrder),
                "net", Json.Obj(
                    "type", string.IsNullOrEmpty(_vm.NetType) ? "virtio" : _vm.NetType,
                    "mode", _vm.NetMode,
                    "relayUrl", RelayUrl()));

            Dictionary<string, object> hda = MediaDescriptor(_vm.HdaPath);
            if (hda != null) config["hda"] = hda;

            Dictionary<string, object> cdrom = MediaDescriptor(_vm.CdromPath);
            if (cdrom != null) config["cdrom"] = cdrom;

            Dictionary<string, object> fda = MediaDescriptor(_vm.FdaPath);
            if (fda != null) config["fda"] = fda;

            if (_stateFiles.ContainsKey("restore.bin"))
                config["initialState"] = VmResourceServer.Origin + "/state/restore.bin";

            if (_form != null && _form.ResourceServer != null && _form.ResourceServer.Share != null)
            {
                config["share"] = Json.Obj(
                    "endpoint", "/share",
                    "readonly", _vm.ShareReadOnly);
            }

            // шрифт текстового режима: у ВМ может быть свой, иначе - хостовый
            string textFont = _vm.TextFont;
            int textSize = _vm.TextFontSize;

            if (string.IsNullOrEmpty(textFont)) textFont = HostSettings.TextFont;
            if (textSize <= 0) textSize = HostSettings.TextFontSize;

            config["textFont"] = textFont ?? "";
            config["textSize"] = textSize;
            config["captureRelease"] = HostSettings.CaptureRelease;

            // реплика стартует с загруженным state'ом и НЕ запускает CPU,
            // пока её не повысят
            if (_replica) config["paused"] = true;

            return config;
        }

        /// <summary>
        /// Куда v86 ходит за произвольным TCP. У реплики своего релея ещё нет,
        /// но порт известен заранее: после повышения она займёт именно его,
        /// поэтому адрес в state остаётся рабочим.
        /// </summary>
        private string RelayUrl()
        {
            int port = _relay != null ? _relay.Port : (_replica ? _replicaRelayPort : 0);

            return port > 0 ? "wisp://127.0.0.1:" + port : "fetch";
        }

        /// <summary>
        /// Поднимает wisp-релей на заданном порту (0 - любой свободный).
        /// Отдельным методом, потому что реплика вызывает его лениво, уже
        /// после повышения - на порту, который был у основной ВМ.
        /// </summary>
        private void StartRelay(int port)
        {
            WispRelay relay = new WispRelay(BuildEgressPolicy(_vm));

            string error;
            if (!relay.Start(port, out error))
            {
                Log.Warn("NET", "wisp relay не поднялся, остаётся fetch: " + error);
                relay.Dispose();
                return;
            }

            _relay = relay;
            Log.Info("NET", _relay.PolicyDescribe());
        }

        /// <summary>Политика egress из vm.json; по умолчанию - без ограничений.</summary>
        private static EgressPolicy BuildEgressPolicy(VmDefinition vm)
        {
            EgressPolicy policy = new EgressPolicy();
            policy.Mode = EgressPolicy.NormalizeMode(vm.EgressMode);

            int dropped;
            policy.Allow = EgressPolicy.ParseRules(vm.EgressAllow ?? new List<string>(), out dropped);

            if (dropped > 0) Log.Warn("NET", "egress: отброшено правил " + dropped);

            return policy;
        }

        private static Dictionary<string, object> MediaDescriptor(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return null;

            long size;
            try { size = new FileInfo(path).Length; }
            catch { return null; }

            return Json.Obj("size", size);
        }

        private static int BootOrderValue(string name)
        {
            switch ((name ?? "").Trim().ToUpperInvariant())
            {
                case "CD_FLOPPY_HARDDISK": return 0x213;
                case "CD_HARDDISK_FLOPPY": return 0x123;
                case "FLOPPY_CD_HARDDISK": return 0x231;
                case "FLOPPY_HARDDISK_CD": return 0x321;
                case "HARDDISK_CD_FLOPPY": return 0x132;
                default: return 0;
            }
        }

        private string ResolveMedia(string slot)
        {
            switch ((slot ?? "").ToLowerInvariant())
            {
                case "hda": return _vm.HdaPath;
                case "cdrom": return _vm.CdromPath;
                case "fda": return _vm.FdaPath;
                default: return null;
            }
        }

        private string ResolveState(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            if (name.IndexOfAny(new[] { '\\', '/', ':' }) >= 0) return null;

            lock (_gate)
            {
                string path;
                return _stateFiles.TryGetValue(name, out path) ? path : null;
            }
        }

        // ---------------------------------------------------------- state save

        public async Task<Dictionary<string, object>> SaveStateAsync(string targetPath, string mode)
        {
            string directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            PendingSave pending = new PendingSave
            {
                Token = Guid.NewGuid().ToString("N").Substring(0, 12),
                Path = targetPath,
                Stream = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.Read)
            };

            lock (_gate) _saves[pending.Token] = pending;

            Log.Info("STATE", "снимаю состояние (" + mode + ") -> " + targetPath);

            _form.Post(Json.Obj("action", "saveState", "token", pending.Token, "mode", mode));

            Task completed = await Task.WhenAny(pending.Completion.Task, Task.Delay(300000)).ConfigureAwait(false);

            lock (_gate) _saves.Remove(pending.Token);

            if (completed != pending.Completion.Task)
            {
                ClosePending(pending);
                try { File.Delete(targetPath); } catch { }
                throw new Hv86Exception("эмулятор не отдал состояние за 5 минут");
            }

            return await pending.Completion.Task.ConfigureAwait(false);
        }

        private void OnStateChunk(Dictionary<string, object> message)
        {
            string token = Json.GetString(message, "token", "");
            int sequence = Json.GetInt(message, "seq", 0);

            PendingSave pending;
            lock (_gate) _saves.TryGetValue(token, out pending);

            if (pending == null)
            {
                Log.Warn("STATE", "кусок без ожидающего сохранения: " + token);
                return;
            }

            try
            {
                if (sequence != pending.NextSequence)
                    throw new Hv86Exception("куски state пришли не по порядку: " + sequence + " != " + pending.NextSequence);

                string data = Json.GetString(message, "data", "");
                byte[] bytes = Convert.FromBase64String(data);

                pending.Stream.Write(bytes, 0, bytes.Length);
                pending.BytesWritten += bytes.Length;
                pending.NextSequence++;

                _form.Post(Json.Obj("action", "ack", "token", token, "seq", sequence));
            }
            catch (Exception ex)
            {
                Log.Exception("STATE", "кусок " + sequence, ex);
                pending.Completion.TrySetException(new Hv86Exception("ошибка приёма state: " + ex.Message));
            }
        }

        private void OnStateDone(Dictionary<string, object> message)
        {
            string token = Json.GetString(message, "token", "");

            PendingSave pending;
            lock (_gate) _saves.TryGetValue(token, out pending);

            if (pending == null) return;

            long size = Json.GetLong(message, "size", pending.BytesWritten);
            long ms = Json.GetLong(message, "ms", 0);
            long instructions = Json.GetLong(message, "instructions", _instructions);

            ClosePending(pending);

            pending.Completion.TrySetResult(Json.Obj(
                "path", pending.Path,
                "size", size,
                "bytes", pending.BytesWritten,
                "ms", ms,
                "instructions", instructions));
        }

        private void OnStateError(Dictionary<string, object> message)
        {
            string token = Json.GetString(message, "token", "");
            string text = Json.GetString(message, "message", "неизвестная ошибка");

            PendingSave pending;
            lock (_gate) _saves.TryGetValue(token, out pending);

            if (pending == null) return;

            ClosePending(pending);
            pending.Completion.TrySetException(new Hv86Exception(text));
        }

        private void ClosePending(PendingSave pending)
        {
            try { pending.Stream?.Flush(true); } catch { }
            try { pending.Stream?.Dispose(); } catch { }
            pending.Stream = null;
        }

        // ------------------------------------------------------- снимки/медиа

        private async Task<Dictionary<string, object>> SnapshotToVmFolderAsync(string name)
        {
            string temp = Path.Combine(Paths.VmDir(_vm.Id), "temp",
                "state-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bin");

            Dictionary<string, object> saved = await SaveStateAsync(temp, "snapshot").ConfigureAwait(false);
            long instructions = Json.GetLong(saved, "instructions", _instructions);

            Dictionary<string, object> snapshot = CheckpointStore.Create(_vm, name, temp, instructions);

            Broadcast("vm.snapshot", Json.Obj("id", _vmId, "snapshot", snapshot));
            Broadcast("ft.event", Json.Obj(
                "id", _vmId,
                "kind", "checkpoint",
                "text", "Снапшот «" + name + "» (" + Math.Round(Json.GetLong(saved, "size", 0) / 1024.0 / 1024.0, 1) +
                        " МБ, " + Json.GetLong(saved, "ms", 0) + " мс)",
                "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));

            return snapshot;
        }

        private async Task AttachAsync(string path, string kind)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new Hv86Exception("файл не найден: " + path);

            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) ext = ".iso";

            string slot = kind == "fda" ? "fda" : "cdrom";
            string target = Path.Combine(Paths.VmMediaDir(_vm.Id), slot + ext);

            File.Copy(path, target, true);

            lock (_gate)
            {
                if (slot == "fda") _vm.FdaFile = Paths.Relative(_vm.Id, target);
                else _vm.CdromFile = Paths.Relative(_vm.Id, target);

                VmStore.Save(_vm);
            }

            _form.Post(Json.Obj(
                "action", "attach",
                "kind", slot,
                "url", "/media/" + slot + "?v=" + DateTime.UtcNow.Ticks,
                "size", new FileInfo(target).Length));

            Broadcast("vm.media", Json.Obj("id", _vmId, "kind", slot, "file", Path.GetFileName(target)));
            Log.Info("MEDIA", "вставлен " + target);
        }

        private async Task DetachAsync(string kind)
        {
            string slot = kind == "fda" ? "fda" : "cdrom";

            _form.Post(Json.Obj("action", "detach", "kind", slot));

            lock (_gate)
            {
                if (slot == "fda") _vm.FdaFile = null;
                else _vm.CdromFile = null;

                VmStore.Save(_vm);
            }

            Broadcast("vm.media", Json.Obj("id", _vmId, "kind", slot, "file", ""));
            await Task.FromResult(0).ConfigureAwait(false);
        }

        private void SaveScreenshot(string dataUrl)
        {
            try
            {
                const string marker = "base64,";
                int index = dataUrl.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (index < 0) return;

                byte[] png = Convert.FromBase64String(dataUrl.Substring(index + marker.Length));

                string dir = Path.Combine(Paths.VmDir(_vm.Id), "screenshots");
                Directory.CreateDirectory(dir);

                string file = Path.Combine(dir,
                    _vm.Name.Replace(' ', '_') + "-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".png");

                File.WriteAllBytes(file, png);

                Broadcast("vm.toast", Json.Obj("id", _vmId, "text", "Screenshot: " + Path.GetFileName(file), "kind", "ok"));
                Log.Info("SNAP", "скриншот " + file);
            }
            catch (Exception ex)
            {
                Log.Warn("SNAP", "скриншот: " + ex.Message);
            }
        }

        // ------------------------------------------------------------- команды

        /// <summary>
        /// Ctrl+V в окне консоли: берём текст из буфера обмена хоста и отдаём
        /// странице - она отправит его гостю нажатиями. Делает именно процесс
        /// ВМ: он живёт в сессии пользователя, а у страницы доступа к системному
        /// буферу нет (и запрос разрешения там выглядел бы странно).
        /// </summary>
        private void PasteFromHost()
        {
            string text;

            try { text = Clipboard.GetText(); }
            catch (Exception ex)
            {
                Log.Warn("CON", "буфер обмена недоступен: " + ex.Message);
                return;
            }

            if (string.IsNullOrEmpty(text))
            {
                Broadcast("vm.toast", Json.Obj("id", _vmId, "text", "Буфер обмена пуст", "kind", "info"));
                return;
            }

            // в гость идёт текст, а не управляющие символы: CRLF -> LF (\n = Enter),
            // табы тоже превращаем в пробелы - их скан-кода у нас нет
            text = text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\t', ' ');

            if (text.Length > MaxPasteChars)
            {
                text = text.Substring(0, MaxPasteChars);
                Log.Warn("CON", "вставка обрезана до " + MaxPasteChars + " символов");
            }

            Log.Info("CON", "вставка из буфера: " + text.Length + " символов");
            _form.Post(Json.Obj("action", "type", "text", text));
        }

        private async Task<object> DispatchAsync(string method, Dictionary<string, object> parameters)
        {
            switch (method)
            {
                case "vm.status":
                    return Json.Obj(
                        "id", _vmId,
                        "state", _state,
                        "error", _error,
                        "instructions", _instructions,
                        "mips", _mips,
                        "pid", System.Diagnostics.Process.GetCurrentProcess().Id,
                        "idle", _idle,
                        "relay_port", _relay == null ? 0 : _relay.Port,
                        "relay_clients", _relay == null ? 0 : _relay.Handshakes);

                case "vm.pause":
                    _form.Post(Json.Obj("action", "freeze"));
                    _state = "paused";
                    return Json.Obj("state", _state);

                case "vm.resume":
                    _form.Post(Json.Obj("action", "thaw"));
                    _state = "running";
                    return Json.Obj("state", _state);

                case "vm.reset":
                    _form.Post(Json.Obj("action", "reset"));
                    _state = "running";
                    return Json.Obj("state", _state);

                // шрифт текстового режима поменяли в интерфейсе: применяем сразу,
                // перезапускать ВМ не нужно (это только отрисовка)
                case "vm.font":
                {
                    string font = Json.GetString(parameters, "font", "");
                    int size = Json.GetInt(parameters, "size", 0);

                    _vm.TextFont = font;
                    _vm.TextFontSize = size;

                    _form.Post(Json.Obj("action", "font", "font", font, "size", size));
                    return Json.Obj("ok", true);
                }

                // окно было открыто без гостя (idle) - служба просит загрузить
                case "vm.boot":
                {
                    if (!_idle) throw new Hv86Exception("ВМ уже загружена");

                    // диск, медиа и сеть могли поменяться, пока окно стояло
                    VmDefinition fresh = VmStore.Load(_vmId);
                    if (fresh != null) _vm = fresh;

                    _idle = false;
                    _forwarder?.Apply(_vm.NetMode == "nat" ? _vm.Forwards : new List<ForwardRule>());

                    _bootConfig = BuildBootConfig();
                    SendBoot();

                    _state = "starting";

                    Log.Info("VMHOST", "загрузка в открытое окно (idle -> boot)");
                    return Json.Obj("ok", true);
                }

                case "vm.show":
                    _form.BeginInvoke(new Action(_form.ShowConsole));
                    return Json.Obj("shown", true);

                // ввод в гостя настоящими нажатиями: страница умеет превращать
                // текст в скан-коды, а служба - нет, поэтому команда идёт сюда.
                // Ждём подтверждения страницы: иначе "введено" было бы враньём
                case "vm.key":
                {
                    string text = Json.GetString(parameters, "text", "");

                    if (string.IsNullOrEmpty(text)) throw new Hv86Exception("пустой ввод");

                    TaskCompletionSource<bool> wait = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);

                    _typeWait = wait;
                    _form.Post(Json.Obj("action", "type", "text", text));

                    Task finished = await Task.WhenAny(wait.Task, Task.Delay(120000)).ConfigureAwait(true);
                    _typeWait = null;

                    if (finished != wait.Task) throw new Hv86Exception("страница не подтвердила ввод за 120 с");
                    if (!await wait.Task.ConfigureAwait(true)) throw new Hv86Exception("страница не смогла набрать текст");

                    Log.Info("CON", "ввод: " + text.Length + " символов");

                    return Json.Obj("ok", true, "chars", text.Length);
                }

                case "vm.hide":
                    _form.BeginInvoke(new Action(_form.HideConsole));
                    return Json.Obj("shown", false);

                case "vm.saveState":
                {
                    string path = Json.GetString(parameters, "path", "");
                    string mode = Json.GetString(parameters, "mode", "snapshot");

                    if (string.IsNullOrEmpty(path)) throw new Hv86Exception("не указан путь для state-файла");

                    return await SaveStateAsync(path, mode).ConfigureAwait(false);
                }

                case "vm.mount":
                    await AttachAsync(Json.GetString(parameters, "path", ""),
                        Json.GetString(parameters, "kind", "cdrom")).ConfigureAwait(false);
                    return Json.Obj("ok", true);

                case "vm.forwards":
                {
                    int dropped;
                    List<ForwardRule> rules = ForwardRule.ParseList(Json.GetList(parameters, "rules"), out dropped);

                    _forwarder?.Apply(_vm.NetMode == "nat" ? rules : new List<ForwardRule>());
                    _vm.Forwards = rules;

                    return Json.Obj("count", rules.Count, "dropped", dropped);
                }

                case "vm.egress":
                {
                    EgressPolicy policy = new EgressPolicy();
                    policy.Mode = EgressPolicy.NormalizeMode(Json.GetString(parameters, "mode", EgressPolicy.ModeAny));

                    List<object> allow = Json.GetList(parameters, "allow");
                    List<string> items = new List<string>();

                    if (allow != null)
                        foreach (object item in allow)
                            if (item is string text) items.Add(text);

                    int dropped;
                    policy.Allow = EgressPolicy.ParseRules(items, out dropped);

                    _relay?.SetPolicy(policy);
                    _egress?.SetPolicy(policy);

                    return Json.Obj("mode", policy.Mode, "rules", policy.Allow.Count, "dropped", dropped);
                }

                // диагностика масштаба экрана: страница применяет режим и отдаёт размер
                case "vm.debug.scale":
                {
                    string mode = Json.GetString(parameters, "mode", "1");

                    TaskCompletionSource<Dictionary<string, object>> probe =
                        new TaskCompletionSource<Dictionary<string, object>>(
                            TaskCreationOptions.RunContinuationsAsynchronously);

                    _scaleProbe = probe;
                    _form.Post(Json.Obj("action", "scaleProbe", "mode", mode));

                    Task finished = await Task.WhenAny(probe.Task, Task.Delay(15000)).ConfigureAwait(false);
                    _scaleProbe = null;

                    if (finished != probe.Task) throw new Hv86Exception("страница не ответила на пробу масштаба");

                    return await probe.Task.ConfigureAwait(false);
                }

                case "vm.eject":
                    await DetachAsync(Json.GetString(parameters, "kind", "cdrom")).ConfigureAwait(false);
                    return Json.Obj("ok", true);

                // диагностика: страница делает настоящий fetch через обёртку
                // egress и возвращает, что вышло (нужно тестам и логам)
                case "vm.debug.fetch":
                {
                    string url = Json.GetString(parameters, "url", "");

                    if (string.IsNullOrEmpty(url)) throw new Hv86Exception("не указан url");

                    TaskCompletionSource<Dictionary<string, object>> probe =
                        new TaskCompletionSource<Dictionary<string, object>>(
                            TaskCreationOptions.RunContinuationsAsynchronously);

                    _fetchProbe = probe;
                    _form.Post(Json.Obj("action", "fetchProbe", "url", url));

                    Task finished = await Task.WhenAny(probe.Task, Task.Delay(30000)).ConfigureAwait(false);
                    _fetchProbe = null;

                    if (finished != probe.Task) throw new Hv86Exception("страница не ответила на fetch-пробу");

                    return await probe.Task.ConfigureAwait(false);
                }

                case "vm.restoreState":
                {
                    string path = Json.GetString(parameters, "path", "");

                    if (string.IsNullOrEmpty(path) || !File.Exists(path))
                        throw new Hv86Exception("state-файл не найден: " + path);

                    const string name = "replica-state.bin";

                    lock (_gate) _stateFiles[name] = path;

                    // ждём именно ответ страницы: «paused» она выставляет ещё
                    // до восстановления, и по нему нельзя понять, готово ли
                    TaskCompletionSource<bool> wait = new TaskCompletionSource<bool>(
                        TaskCreationOptions.RunContinuationsAsynchronously);

                    _restoreWait = wait;
                    _form.Post(Json.Obj("action", "restore", "url", VmResourceServer.Origin + "/state/" + name));

                    Task finished = await Task.WhenAny(wait.Task, Task.Delay(180000)).ConfigureAwait(false);
                    _restoreWait = null;

                    if (finished != wait.Task) throw new Hv86Exception("страница не восстановила состояние за 180 с");
                    if (!await wait.Task.ConfigureAwait(false)) throw new Hv86Exception("восстановление состояния не удалось");

                    Log.Info("VMHOST", "реплика приняла состояние (" + (new FileInfo(path).Length / 1024 / 1024) + " МБ)");

                    return Json.Obj("ok", true, "instructions", _instructions);
                }

                case "vm.promote":
                {
                    if (_promoted) return Json.Obj("ok", true);
                    _promoted = true;

                    // сеть возвращаем на себя: релей занимает порт основной ВМ,
                    // правила проброса снова применяются к живому гостю
                    if (_vm.NetMode == "relay" && _relay == null)
                        StartRelay(_replicaRelayPort);

                    _forwarder.Apply(_vm.NetMode == "nat" ? _vm.Forwards : new List<ForwardRule>());

                    _form.Post(Json.Obj("action", "thaw"));
                    Log.Info("VMHOST", "реплика повышена до основной (порт релея " +
                                       (_relay == null ? "нет" : _relay.Port.ToString(CultureInfo.InvariantCulture)) + ")");

                    return Json.Obj("ok", true, "relay_port", _relay == null ? 0 : _relay.Port);
                }

                case "vm.shutdown":
                    _ = Task.Run(() => ShutdownAsync("service"));
                    return Json.Obj("ok", true);

                // диагностика: умеет ли страница синхронный XHR к /share
                // (на этом держится общая папка в web-движке)
                case "vm.debug.xhr":
                {
                    TaskCompletionSource<Dictionary<string, object>> probe =
                        new TaskCompletionSource<Dictionary<string, object>>(
                            TaskCreationOptions.RunContinuationsAsynchronously);

                    _xhrProbe = probe;
                    _form.Post(Json.Obj("action", "xhrTest"));

                    Task finished = await Task.WhenAny(probe.Task, Task.Delay(15000)).ConfigureAwait(false);
                    _xhrProbe = null;

                    if (finished != probe.Task) throw new Hv86Exception("страница не ответила на xhr-пробу");

                    return await probe.Task.ConfigureAwait(false);
                }
            }

            throw new Hv86Exception("неизвестный метод: " + method);
        }

        // -------------------------------------------------------------- shutdown

        /// <summary>
        /// Штатная остановка: заморозить, снять state, применить «грязные»
        /// блоки диска к образу, уничтожить эмулятор, закрыть окно.
        /// </summary>
        public async Task ShutdownAsync(string reason)
        {
            if (_shuttingDown) return;
            _shuttingDown = true;

            Log.Info("VMHOST", "остановка (" + reason + ")");

            string temp = Path.Combine(Paths.VmDir(_vm.Id), "temp",
                "shutdown-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".bin");

            try
            {
                await FreezeAndDestroyAsync(temp).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Exception("VMHOST", "остановка", ex);
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }

            _state = "stopped";
            Broadcast("vm.state", Json.Obj("id", _vmId, "state", _state, "error", _error));

            try { _forwarder?.Dispose(); } catch { }
            _forwarder = null;

            try { _relay?.Dispose(); } catch { }
            _relay = null;

            try { _server?.Dispose(); } catch { }
            _server = null;

            if (_form != null && _form.IsHandleCreated)
                _ = _form.BeginInvoke(new Action(_form.CloseForReal));
        }

        /// <summary>Снять состояние, применить блоки диска и погасить эмулятор.</summary>
        private async Task<Dictionary<string, object>> FreezeAndDestroyAsync(string statePath)
        {
            _form.Post(Json.Obj("action", "freeze"));
            await Task.Delay(150).ConfigureAwait(true);

            Dictionary<string, object> saved;

            try
            {
                saved = await SaveStateAsync(statePath, "shutdown").ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Warn("VMHOST", "state при остановке не снят: " + ex.Message);
                _form.Post(Json.Obj("action", "destroy"));
                await WaitStateAsync("stopped", 5000).ConfigureAwait(true);
                return null;
            }

            long size = Json.GetLong(saved, "size", 0);
            Log.Info("STATE", "state при остановке: " + (size / 1024 / 1024) + " МБ, " +
                              Json.GetLong(saved, "ms", 0) + " мс");

            try
            {
                string disk = _vm.HdaPath;

                if (!string.IsNullOrEmpty(disk) && File.Exists(disk))
                {
                    int blocks = V86State.ApplyDiskBlocks(statePath, disk);
                    Log.Info("DISK", "применено блоков: " + blocks);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("DISK", "сохранение диска", ex);
            }

            _form.Post(Json.Obj("action", "destroy"));
            await WaitStateAsync("stopped", 10000).ConfigureAwait(true);

            return saved;
        }

        private async Task WaitStateAsync(string expected, int timeoutMs)
        {
            await WaitStateReachedAsync(expected, timeoutMs).ConfigureAwait(true);
        }

        private async Task<bool> WaitStateReachedAsync(string expected, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                if (_state == expected) return true;
                await Task.Delay(100).ConfigureAwait(true);
            }

            return false;
        }

        // ----------------------------------------------------------------- smoke

        /// <summary>
        /// Самотест: загрузка гостя → state → перезапуск из state → рост
        /// счётчика инструкций. Проверяет всю цепочку сразу: WebView2, v86,
        /// отдачу образов по Range, приём state кусками и восстановление.
        /// </summary>
        private async Task RunSmokeAsync()
        {
            Log.Info("SMOKE", "ожидаю загрузку гостя ...");

            bool booted = await WaitUntilAsync(delegate { return _instructions > 3000000; }, 60000).ConfigureAwait(true);

            if (!booted)
            {
                Log.Error("SMOKE", "гость не дошёл до 3M инструкций (instructions=" + _instructions + ")");
                FinishSmoke(1);
                return;
            }

            long before = _instructions;
            Log.Info("SMOKE", "гость исполняет: " + _instructions + " инструкций, " + _mips + " MIPS");

            string statePath = Path.Combine(Paths.Root, "smoke-state.bin");

            Dictionary<string, object> saved;

            try
            {
                saved = await SaveStateAsync(statePath, "smoke").ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                Log.Error("SMOKE", "save_state: " + ex.Message);
                FinishSmoke(1);
                return;
            }

            long size = Json.GetLong(saved, "size", 0);
            long ms = Json.GetLong(saved, "ms", 0);

            Log.Info("SMOKE", "state: " + size + " байт (" + (size / 1024) + " КБ) за " + ms + " мс");

            if (size < 1024 * 1024)
            {
                Log.Error("SMOKE", "state подозрительно мал");
                FinishSmoke(1);
                return;
            }

            // проверим формат своим же разборщиком: если он не понимает файл,
            // значит и сохранение диска работать не будет
            try
            {
                V86State.Info info = V86State.Read(statePath);
                int blocks = V86State.CountDiskBlocks(statePath);
                Log.Info("SMOKE", "разбор state: буферов " + info.Offsets.Count + ", блоков диска " + blocks);
            }
            catch (Exception ex)
            {
                Log.Error("SMOKE", "разбор state: " + ex.Message);
                FinishSmoke(1);
                return;
            }

            lock (_gate) _stateFiles["restore.bin"] = statePath;

            _form.Post(Json.Obj("action", "freeze"));
            await Task.Delay(150).ConfigureAwait(true);
            _form.Post(Json.Obj("action", "destroy"));
            await WaitStateAsync("stopped", 15000).ConfigureAwait(true);

            _instructions = 0;
            _bootConfig = BuildBootConfig();
            Log.Info("SMOKE", "перезапуск из state ...");
            SendBoot();

            bool restored = await WaitUntilAsync(delegate { return _instructions > 500000; }, 60000).ConfigureAwait(true);

            if (!restored)
            {
                Log.Error("SMOKE", "восстановленная ВМ не исполняет инструкции");
                FinishSmoke(1);
                return;
            }

            Log.Info("SMOKE", "восстановлено: " + _instructions + " инструкций (до снапшота было " + before + ")");

            FinishSmoke(0);
        }

        private void FinishSmoke(int code)
        {
            _exitCode = code;
            _shuttingDown = true;

            Log.Info("SMOKE", code == 0 ? "OK" : "FAIL (" + code + ")");

            try { _server?.Dispose(); } catch { }
            _server = null;

            if (_form != null && _form.IsHandleCreated)
                _ = _form.BeginInvoke(new Action(_form.CloseForReal));
        }

        private async Task<bool> WaitUntilAsync(Func<bool> condition, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                if (_shuttingDown) return false;
                if (condition()) return true;
                await Task.Delay(200).ConfigureAwait(true);
            }

            return false;
        }

        // -------------------------------------------------------------- helpers

        private string BuildConsoleHtml()
        {
            string html = Resources.Text(Resources.ConsoleHtml);

            if (string.IsNullOrEmpty(html))
                return "<!doctype html><html><body style='font:14px Consolas;background:#05070b;color:#dbe6f7;padding:24px'>" +
                       "console.html не вшит в exe</body></html>";

            html = html.Replace("/*{{THEME}}*/", Resources.Text(Resources.ThemeCss));
            html = html.Replace("//{{JS}}", Resources.Text(Resources.ConsoleJs));
            html = html.Replace("{{VERSION}}", AppInfo.V86Version);

            return html;
        }

        private void Broadcast(string eventName, object data)
        {
            try { _server?.Broadcast(eventName, data); } catch { }
        }
    }
}

