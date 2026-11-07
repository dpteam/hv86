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

            _manager.StartAutostart();
        }

        public void Stop()
        {
            try { _timer?.Dispose(); } catch { }
            _timer = null;

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
                    return status;
                }

                case "host.log":
                    return Json.Obj("text", Log.Tail(Json.GetInt(parameters, "lines", 200)));

                // настройки хоста: сейчас только шрифт текстового режима
                case "host.settings":
                    return HostSettings.View();

                case "host.settings.set":
                    return HostSettings.Apply(parameters);

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