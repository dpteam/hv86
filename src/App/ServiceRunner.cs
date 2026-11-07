using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Hv86.Core;
using Hv86.Vm;

namespace Hv86.App
{
    /// <summary>
    /// Движок службы: реестр ВМ + канал управления + сторож.
    ///
    /// Здесь нет ни одной строки про окна: служба живёт в сессии 0, и всё
    /// визуальное - в процессах vmhost, запущенных в сессии пользователя.
    /// </summary>
    public sealed class ServiceRunner
    {
        private VmManager _manager;
        private PipeServer _server;
        private Timer _timer;
        private ReplicaServer _replica;
        private int _replicaPort;
        private readonly object _leaseGate = new object();
        private Timer _autopromote;
        private readonly Dictionary<string, string> _leaseOk = new Dictionary<string, string>();
        private readonly Dictionary<string, DateTime> _leaseOkUntil = new Dictionary<string, DateTime>();
        private bool _promoting;
        private readonly DateTime _startedUtc = DateTime.UtcNow;

        public void Start()
        {
            Paths.EnsureDirectories();
            Log.Info("SVC", "данные: " + Paths.Root + "; " + SessionLauncher.Describe());

            _manager = new VmManager();
            _manager.Event += delegate (string name, object data)
            {
                try { _server?.Broadcast(name, data); } catch { }
            };

            _server = new PipeServer(PipeNames.Service, DispatchAsync);
            _server.Start();

            _timer = new Timer(delegate { try { _manager.Tick(); } catch (Exception ex) { Log.Exception("FT", "тик", ex); } },
                null, 2000, 2000);

            // автоматический подъём реплики: отдельный таймер, чтобы не
            // смешивать с чекпоинтами и сторожем
            _autopromote = new Timer(delegate
            {
                try { AutoPromoteTick(); }
                catch (Exception ex) { Log.Exception("FT", "авто-подъём", ex); }
            }, null, 3000, 2000);

            ApplyReplicaListener();

            _manager.StartAutostart();
        }

        /// <summary>
        /// Приём реплик с другой машины: включается настройкой хоста, а не
        /// флагом командной строки - служба и так знает свой hv86.json.
        /// </summary>
        private void ApplyReplicaListener()
        {
            int port = HostSettings.ReplicaPort;
            string token = HostSettings.ReplicaToken;

            if (_replica != null && _replicaPort == port && port > 0) return;

            try { _replica?.Dispose(); } catch { }
            _replica = null;
            _replicaPort = 0;

            if (port <= 0) return;

            if (token.Length == 0)
            {
                Log.Warn("REPL", "порт реплики " + port + " задан, но токен пуст: приём не включён");
                return;
            }

            try
            {
                ReplicaServer server = new ReplicaServer(ReplicaStore.Default(), port, token, true,
                    LeaseCurrent);
                server.Start();

                _replica = server;
                _replicaPort = port;

                Log.Info("REPL", "приём реплик включён на порту " + port);
            }
            catch (Exception ex)
            {
                Log.Error("REPL", "не удалось включить приём реплик: " + ex.Message);
            }
        }

        /// <summary>
        /// Действительна ли аренда, с которой пришёл state. Спрашиваем свидетеля
        /// (с кэшем на пару секунд: state летит каждые 15 с, лишние запросы ни к
        /// чему). Свидетель не настроен - проверки нет, как и раньше.
        /// </summary>
        private bool LeaseCurrent(string vmId, string lease)
        {
            string url = HostSettings.WitnessUrl;

            if (string.IsNullOrEmpty(url)) return true;
            if (string.IsNullOrEmpty(lease)) return false;

            lock (_leaseGate)
            {
                string known;
                DateTime until;

                if (_leaseOk.TryGetValue(vmId, out known) && known == lease &&
                    _leaseOkUntil.TryGetValue(vmId, out until) && DateTime.UtcNow < until)
                    return true;
            }

            try
            {
                Dictionary<string, object> reply = WitnessClient.CheckAsync(url, HostSettings.WitnessToken,
                    vmId, lease, 3000).GetAwaiter().GetResult();

                bool current = Json.GetBool(reply, "current", false);

                lock (_leaseGate)
                {
                    _leaseOk[vmId] = lease;
                    _leaseOkUntil[vmId] = DateTime.UtcNow.AddSeconds(2);
                }

                if (!current) Log.Warn("WIT", "state с недействительной арендой отклонён: " + vmId);

                return current;
            }
            catch (Exception ex)
            {
                // свидетель молчит - не принимаем: безопасность важнее полноты
                Log.Warn("WIT", "аренду проверить не удалось (" + vmId + "): " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Автоматический подъём: если реплика прогрета, основной разрешил
        /// авто-подъём, а аренда освободилась - занимаем её и поднимаемся.
        /// </summary>
        private void AutoPromoteTick()
        {
            if (_manager == null) return;

            string url = HostSettings.WitnessUrl;
            if (string.IsNullOrEmpty(url)) return;

            lock (_leaseGate)
            {
                if (_promoting) return;
            }

            foreach (Dictionary<string, object> replica in _manager.LocalReplicas())
            {
                if (!Json.GetBool(replica, "warm", false)) continue;
                if (!Json.GetBool(replica, "auto_promote", false)) continue;

                string vmId = Json.GetString(replica, "vm_id", "");
                if (vmId.Length == 0) continue;

                Dictionary<string, object> claim;

                try
                {
                    claim = WitnessClient.ClaimAsync(url, HostSettings.WitnessToken, vmId,
                        Environment.MachineName, 12, 5000).GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    Log.Warn("WIT", "claim " + vmId + ": " + ex.Message);
                    continue;
                }

                if (!Json.GetBool(claim, "granted", false)) continue;

                string leaseToken = Json.GetString(claim, "token", "");

                lock (_leaseGate) _promoting = true;

                Log.Warn("FT", "аренда " + vmId + " свободна: поднимаю реплику автоматически");

                Task.Run(async delegate
                {
                    try
                    {
                        await _manager.PromoteReplicaAsync(vmId, leaseToken).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("FT", "авто-подъём " + vmId + ": " + ex.Message);
                    }
                    finally
                    {
                        lock (_leaseGate) _promoting = false;
                    }
                });

                return;
            }
        }

        public void Stop()
        {
            try { _timer?.Dispose(); } catch { }
            _timer = null;

            try { _autopromote?.Dispose(); } catch { }
            _autopromote = null;

            try { _replica?.Dispose(); } catch { }
            _replica = null;
            _replicaPort = 0;

            try { _manager?.StopAll(); } catch (Exception ex) { Log.Exception("SVC", "остановка ВМ", ex); }

            try { _server?.Dispose(); } catch { }
            _server = null;

            try { _manager?.Dispose(); } catch { }
            _manager = null;

            Log.Info("SVC", "движок остановлен");
        }

        private async Task<object> DispatchAsync(string method, Dictionary<string, object> parameters)
        {
            if (_manager == null) throw new Hv86Exception("движок не запущен");

            string id = Json.GetString(parameters, "id", "");

            switch (method)
            {
                case "host.ping":
                    return Json.Obj("pong", true, "version", AppInfo.Version);

                case "host.status":
                {
                    Dictionary<string, object> status = _manager.HostStatus();
                    status["service_started_utc"] = _startedUtc.ToString("o");
                    status["clients"] = _server == null ? 0 : _server.ClientCount;
                    status["replica_url"] = _replica == null ? "" : _replica.BaseUrl;
                    return status;
                }

                case "host.log":
                    return Json.Obj("text", Log.Tail(Json.GetInt(parameters, "lines", 200)));

                // настройки хоста: шрифт текстового режима, захват мыши, приём реплик
                case "host.settings":
                    return HostSettings.View();

                case "host.settings.set":
                {
                    Dictionary<string, object> view = HostSettings.Apply(parameters);

                    // порт/токен могли поменять - перезапускаем приёмник
                    ApplyReplicaListener();

                    return view;
                }

                // шрифты текстового режима: системные + загруженные пользователем
                case "host.fonts":
                    return Json.Obj(
                        "system", FontCatalog.SystemFonts(),
                        "custom", FontCatalog.Custom(),
                        "all", FontCatalog.All());

                case "host.fonts.import":
                    return FontCatalog.Import(Json.GetString(parameters, "path", ""));

                case "host.fonts.remove":
                    FontCatalog.Remove(Json.GetString(parameters, "file", ""));
                    return Json.Obj("ok", true);

                // локальные реплики, принятые этим хостом
                case "host.replicas":
                    return _manager.LocalReplicas();

                case "vm.list":
                    return _manager.ListView();

                case "vm.presets":
                    return _manager.Presets();

                case "vm.get":
                    return _manager.GetView(id);

                case "vm.create":
                    return await _manager.CreateAsync(parameters).ConfigureAwait(false);

                case "vm.start":
                    return await _manager.StartAsync(id, false, Json.GetString(parameters, "restore", null)).ConfigureAwait(false);

                case "vm.stop":
                    return await _manager.StopAsync(id, false).ConfigureAwait(false);

                case "vm.pause":
                    return await _manager.PauseAsync(id).ConfigureAwait(false);

                case "vm.resume":
                    return await _manager.ResumeAsync(id).ConfigureAwait(false);

                case "vm.reset":
                    return await _manager.ResetAsync(id).ConfigureAwait(false);

                case "vm.keys":
                    return await _manager.KeysAsync(id, Json.GetString(parameters, "text", ""),
                        Json.GetBool(parameters, "enter", false)).ConfigureAwait(false);

                case "vm.console":
                    return await _manager.ConsoleAsync(id, Json.GetBool(parameters, "start", true))
                        .ConfigureAwait(false);

                case "vm.hide":
                    return await _manager.HideAsync(id).ConfigureAwait(false);

                case "vm.delete":
                    return await _manager.DeleteAsync(id).ConfigureAwait(false);

                case "vm.snapshot":
                    return await _manager.SnapshotAsync(id, Json.GetString(parameters, "name", "")).ConfigureAwait(false);

                case "vm.snapshots":
                    return _manager.Snapshots(id);

                case "vm.snapshot.delete":
                    return await _manager.DeleteSnapshotAsync(id, Json.GetString(parameters, "snapshot", ""))
                        .ConfigureAwait(false);

                case "vm.revert":
                    return await _manager.RevertAsync(id, Json.GetString(parameters, "snapshot", "")).ConfigureAwait(false);

                case "vm.mount":
                    return await _manager.MountAsync(id, Json.GetString(parameters, "path", ""),
                        Json.GetString(parameters, "kind", "cdrom")).ConfigureAwait(false);

                case "vm.eject":
                    return await _manager.EjectAsync(id, Json.GetString(parameters, "kind", "cdrom")).ConfigureAwait(false);

                case "vm.floppy":
                    return await _manager.FloppyAsync(id,
                        Json.GetInt(parameters, "delta", 0),
                        Json.GetInt(parameters, "index", -1)).ConfigureAwait(false);

                case "vm.migrate":
                    return await _manager.MigrateAsync(id).ConfigureAwait(false);

                case "vm.promote-replica":
                    return await _manager.PromoteReplicaAsync(
                        Json.GetString(parameters, "replica", id)).ConfigureAwait(false);

                case "vm.update":
                    return await _manager.UpdateAsync(id, parameters).ConfigureAwait(false);

                case "vm.clone":
                    return await _manager.CloneAsync(id, Json.GetString(parameters, "name", "")).ConfigureAwait(false);

                case "vm.export":
                    return await _manager.ExportAsync(id, Json.GetString(parameters, "path", ""),
                        Json.GetBool(parameters, "snapshots", false)).ConfigureAwait(false);

                case "vm.import":
                    return await _manager.ImportAsync(Json.GetString(parameters, "path", "")).ConfigureAwait(false);

                case "host.diagnostics":
                {
                    Dictionary<string, object> status = _manager.HostStatus();
                    return Diagnostics.Collect(status, _manager.ListView());
                }

                // диагностика: имитация падения vmhost (для тестов FT)
                case "vm.debug.kill":
                    _manager.KillHost(id);
                    return Json.Obj("killed", true);
            }

            throw new Hv86Exception("неизвестный метод: " + method);
        }
    }
}