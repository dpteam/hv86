using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>
    /// Реестр ВМ и все операции над ними. Живёт в службе (и однажды - в
    /// автономном режиме без службы). Наружу торчит только через pipe-RPC,
    /// поэтому все методы возвращают словари, а не типы.
    ///
    /// Отказоустойчивость собрана здесь же:
    ///   * Tick() - сторож: heartbeat, зависания, рестарт по политике;
    ///   * кольцо чекпоинтов по таймеру (ft.interval_ms);
    ///   * при рестарте после падения ВМ поднимается со свежего чекпоинта.
    /// </summary>
    public sealed class VmManager : IDisposable
    {
        private readonly object _gate = new object();
        private readonly Dictionary<string, VmRuntime> _vms =
            new Dictionary<string, VmRuntime>(StringComparer.Ordinal);

        /// <summary>Предел ввода за один вызов: больше - это уже не текст.</summary>
        private const int MaxKeys = 4096;

        private readonly DateTime _startedUtc = DateTime.UtcNow;
        private bool _disposed;

        /// <summary>Событие для UI: имя события + данные.</summary>
        public event Action<string, object> Event;

        private sealed class VmRuntime
        {
            public VmDefinition Def;
            public VmProcess Proc;
            public DateTime StartedUtc = DateTime.UtcNow;
            public DateTime LastBeatUtc = DateTime.UtcNow;
            public long Instructions;
            public int Mips;
            public int RelayPort;
            public int RelayClients;
            public bool RingBusy;
            public DateTime NextRingUtc = DateTime.UtcNow;
            public bool Restarting;
            public int ConsecutiveFailures;
            public string LastRestore;

            // горячая реплика (ft.mode=replica): второй процесс той же ВМ,
            // который стоит на паузе с последним чекпоинтом и ждёт повышения
            public VmProcess Replica;
            public string ReplicaState = "";
            public DateTime ReplicaUtc = DateTime.UtcNow;
            public bool ReplicaStarting;

            // удалённая реплика (ft.mode=remote): образ и state уезжают по сети
            public string RemoteState = "";
            public DateTime RemoteUtc = DateTime.UtcNow;
            public string RemoteError = "";
            public long RemoteSeq;
            public bool RemoteBusy;

            // аренда у свидетеля: пока она жива, эта машина - единственный хозяин
            public bool LeaseHeld;
            public string LeaseToken = "";
            public DateTime LeaseExpiresUtc;
            public DateTime LeaseRenewUtc;
            public string LeaseError = "";

            // окно, открытое для выключенной ВМ: гостя внутри нет, но к нему
            // можно подключиться (как в Hyper-V) и запустить оттуда
            public VmProcess Idle;
        }

        /// <summary>TTL аренды у свидетеля, секунды (docs/replica-quorum.md).</summary>
        private const int LeaseTtlSec = 12;

        /// <summary>Как часто продлеваем аренду: три попытки до истечения.</summary>
        private const int LeaseRenewSec = 4;

        public VmManager()
        {
            LoadAll();
        }

        // ------------------------------------------------------------- загрузка

        private void LoadAll()
        {
            foreach (VmDefinition def in VmStore.LoadAll())
            {
                // после перезапуска службы ни одна ВМ не работает: процессы
                // vmhost были убиты вместе с job object'ом
                if (def.State != "stopped" && def.State != "failed")
                {
                    Log.Warn("VM", "ВМ " + def.Id + " была в состоянии " + def.State + ", считаю остановленной");
                    def.State = "stopped";
                    try { VmStore.Save(def); } catch { }
                }

                lock (_gate)
                {
                    _vms[def.Id] = new VmRuntime
                    {
                        Def = def,
                        NextRingUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(2000, def.FtIntervalMs))
                    };
                }
            }

            Log.Info("VM", "загружено ВМ: " + _vms.Count);
        }

        public void StartAutostart()
        {
            foreach (VmRuntime runtime in Snapshot())
            {
                if (!runtime.Def.Autostart) continue;

                string id = runtime.Def.Id;
                Log.Info("VM", "автозапуск " + id);

                Task.Run(async () =>
                {
                    try { await StartAsync(id, false, null).ConfigureAwait(false); }
                    catch (Exception ex) { Log.Exception("VM", "автозапуск " + id, ex); }
                });
            }
        }

        // --------------------------------------------------------------- запросы

        public Dictionary<string, object> HostStatus()
        {
            int running = 0;
            int total = 0;

            foreach (VmRuntime runtime in Snapshot())
            {
                total++;
                if (runtime.Def.State == "running" || runtime.Def.State == "paused") running++;
            }

            return Json.Obj(
                "product", AppInfo.ProductName,
                "version", AppInfo.Version,
                "v86", AppInfo.V86Version,
                "uptime_sec", (long)(DateTime.UtcNow - _startedUtc).TotalSeconds,
                "vms_total", total,
                "vms_running", running,
                "user", Elevation.CurrentUser,
                "session", System.Diagnostics.Process.GetCurrentProcess().SessionId,
                "active_session", SessionLauncher.ActiveConsoleSessionId,
                "data_dir", Paths.Root);
        }

        public List<object> ListView()
        {
            List<object> list = new List<object>();

            foreach (VmRuntime runtime in Snapshot())
            {
                Dictionary<string, object> view = runtime.Def.ToView();

                view["instructions"] = runtime.Instructions;
                view["mips"] = runtime.Mips;
                view["uptime_sec"] = runtime.StartedUtc == default(DateTime)
                    ? 0
                    : (long)(DateTime.UtcNow - runtime.StartedUtc).TotalSeconds;
                view["checkpoints"] = CheckpointStore.RingCount(runtime.Def);
                view["alive"] = runtime.Proc != null && !runtime.Proc.HasExited;
                view["relay_port"] = runtime.RelayPort;
                view["relay_clients"] = runtime.RelayClients;
                view["pipe"] = runtime.Proc == null ? "" : runtime.Proc.PipeName;
                view["replica"] = runtime.ReplicaState;
                view["remote_state"] = runtime.RemoteState;
                view["remote_age_sec"] = runtime.RemoteUtc == default(DateTime)
                    ? 0
                    : (long)Math.Max(0, (DateTime.UtcNow - runtime.RemoteUtc).TotalSeconds);
                view["remote_error"] = runtime.RemoteError;
                view["idle"] = runtime.Idle != null && !runtime.Idle.HasExited;

                list.Add(view);
            }

            return list;
        }

        public Dictionary<string, object> GetView(string id)
        {
            VmRuntime runtime = Get(id);
            Dictionary<string, object> view = runtime.Def.ToView();
            view["instructions"] = runtime.Instructions;
            view["mips"] = runtime.Mips;
            view["checkpoints"] = CheckpointStore.RingCount(runtime.Def);
            view["alive"] = runtime.Proc != null && !runtime.Proc.HasExited;
            view["relay_port"] = runtime.RelayPort;
            view["relay_clients"] = runtime.RelayClients;
            view["pipe"] = runtime.Proc == null ? "" : runtime.Proc.PipeName;
            view["replica"] = runtime.ReplicaState;
            view["replica_alive"] = runtime.Replica != null && !runtime.Replica.HasExited;
            view["remote_state"] = runtime.RemoteState;
            view["remote_age_sec"] = runtime.RemoteUtc == default(DateTime)
                ? 0
                : (long)Math.Max(0, (DateTime.UtcNow - runtime.RemoteUtc).TotalSeconds);
            view["remote_error"] = runtime.RemoteError;
            view["idle"] = runtime.Idle != null && !runtime.Idle.HasExited;
            return view;
        }

        /// <summary>
        /// Реплики, принятые этим хостом (для оператора: что у нас лежит и
        /// насколько свежее). Смотреть их имеет смысл только на реплике.
        /// </summary>
        public List<object> LocalReplicas()
        {
            List<object> list = new List<object>();
            ReplicaStore store = ReplicaStore.Default();

            try
            {
                if (!System.IO.Directory.Exists(store.Root)) return list;

                foreach (string dir in System.IO.Directory.GetDirectories(store.Root))
                {
                    string id = System.IO.Path.GetFileName(dir);
                    if (!ReplicaStore.IsSafeId(id)) continue;

                    try { list.Add(store.Status(id)); }
                    catch (Exception ex) { Log.Warn("REPL", "реплика " + id + ": " + ex.Message); }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("REPL", "список реплик: " + ex.Message);
            }

            return list;
        }

        public List<object> Presets()
        {
            List<object> list = new List<object>();

            foreach (Dictionary<string, object> preset in PresetCatalog.All())
            {
                list.Add(Json.Obj(
                    "id", Json.GetString(preset, "id", ""),
                    "name", Json.GetString(preset, "name", ""),
                    "description", Json.GetString(preset, "description", ""),
                    "kind", Json.GetString(preset, "kind", ""),
                    "memory_mb", Json.GetInt(preset, "memory_mb", 128),
                    "vga_memory_mb", Json.GetInt(preset, "vga_memory_mb", 8),
                    "disk_mb", Json.GetInt(preset, "disk_mb", 0),
                    "boot", Json.GetString(preset, "boot", "auto"),
                    "size", Json.GetLong(preset, "size", 0),
                    "floppy_set", Json.GetBool(preset, "floppy_set", false),
                    "floppy_count", Json.GetInt(preset, "floppy_count", 0),
                    "source", Json.GetString(preset, "source", "")));
            }

            return list;
        }

        // --------------------------------------------------------------- создание

        public async Task<object> CreateAsync(Dictionary<string, object> parameters)
        {
            string name = (Json.GetString(parameters, "name", "") ?? "").Trim();
            string presetId = Json.GetString(parameters, "preset", "");
            string isoPath = Json.GetString(parameters, "iso_path", "");

            Dictionary<string, object> preset = PresetCatalog.Get(presetId);
            List<string> presetFiles = new List<string>();

            if (preset != null)
            {
                Dictionary<string, object> captured = preset;
                presetFiles = await Task.Run(() => PresetCatalog.EnsureLocalFiles(captured)).ConfigureAwait(false);
            }

            string presetFile = presetFiles.Count > 0 ? presetFiles[0] : null;

            int memoryMb = Json.GetInt(parameters, "memory_mb", 0);
            if (memoryMb <= 0) memoryMb = preset == null ? 128 : Json.GetInt(preset, "memory_mb", 128);
            memoryMb = RoundPow2(Math.Max(16, Math.Min(1024, memoryMb)));

            int vgaMb = Json.GetInt(parameters, "vga_memory_mb", 0);
            if (vgaMb <= 0) vgaMb = preset == null ? 8 : Json.GetInt(preset, "vga_memory_mb", 8);
            vgaMb = Math.Max(2, Math.Min(32, vgaMb));

            int diskMb = Json.GetInt(parameters, "disk_mb", -1);
            if (diskMb < 0) diskMb = preset == null ? 128 : Json.GetInt(preset, "disk_mb", 0);

            diskMb = Math.Max(0, Math.Min(2048, diskMb));

            VmDefinition vm = new VmDefinition();
            vm.Id = VmStore.NewId();
            vm.Name = name.Length > 0 ? name : (preset == null ? "VM " + vm.Id : Json.GetString(preset, "name", "VM"));
            vm.MemoryMb = memoryMb;
            vm.VgaMemoryMb = vgaMb;
            vm.Autostart = Json.GetBool(parameters, "autostart", false);
            vm.Engine = Json.GetString(parameters, "engine", "web") == "headless" ? "headless" : "web";
            vm.NetMode = Json.GetString(parameters, "net_mode", "nat") == "relay" ? "relay" : "nat";

            // сетевая карта: у старых гостей (DOS/Windows) virtio нет вообще,
            // поэтому в каталоге copy.sh по умолчанию ne2k - как на сайте
            string presetNet = preset == null ? "" : Json.GetString(preset, "net", "");
            if (presetNet == "virtio" || presetNet == "ne2k") vm.NetType = presetNet;
            else if (preset != null && Json.GetString(preset, "source", "") == "copy.sh") vm.NetType = "ne2k";

            ApplyShare(vm, parameters);
            ApplyEgress(vm, parameters);
            vm.BootOrder = preset == null ? "auto" : Json.GetString(preset, "boot", "auto");
            vm.HdaFile = null;
            vm.CdromFile = null;
            vm.FdaFile = null;
            vm.State = "stopped";

            Paths.EnsureVmDirectories(vm.Id);

            string kind = preset == null ? "" : Json.GetString(preset, "kind", "");

            try
            {
                if (presetFile != null)
                {
                    if (kind == "fda" && presetFiles.Count > 1)
                    {
                        // комплект дискет (установочные диски DOS): все образы
                        // лежат рядом, в консоли можно «вставить следующую»
                        vm.FdaSet = new List<string>();

                        for (int i = 0; i < presetFiles.Count; i++)
                        {
                            string partExt = ImageExtension(presetFiles[i]);
                            string part = Path.Combine(Paths.VmMediaDir(vm.Id),
                                "floppy-" + (i + 1) + partExt);

                            File.Copy(presetFiles[i], part, true);
                            vm.FdaSet.Add(Paths.Relative(vm.Id, part));
                        }

                        vm.FdaIndex = 0;
                        vm.FdaFile = vm.FdaSet[0];

                        Log.Info("VM", "набор дискет: " + vm.FdaSet.Count + " шт");
                    }
                    else
                    {
                        string ext = ImageExtension(presetFile);

                        if (kind == "fda")
                        {
                            string target = Path.Combine(Paths.VmMediaDir(vm.Id), "floppy" + ext);
                            File.Copy(presetFile, target, true);
                            vm.FdaFile = Paths.Relative(vm.Id, target);
                        }
                        else if (kind == "hda")
                        {
                            string target = Path.Combine(Paths.VmDisksDir(vm.Id), "system" + ext);
                            File.Copy(presetFile, target, true);
                            vm.HdaFile = Paths.Relative(vm.Id, target);
                        }
                        else
                        {
                            string target = Path.Combine(Paths.VmMediaDir(vm.Id), "boot" + ext);
                            File.Copy(presetFile, target, true);
                            vm.CdromFile = Paths.Relative(vm.Id, target);
                        }
                    }
                }

                if (!string.IsNullOrEmpty(isoPath))
                {
                    if (!File.Exists(isoPath)) throw new Hv86Exception("ISO не найден: " + isoPath);

                    string target = Path.Combine(Paths.VmMediaDir(vm.Id), "install.iso");
                    File.Copy(isoPath, target, true);
                    vm.CdromFile = Paths.Relative(vm.Id, target);

                    if (string.IsNullOrEmpty(vm.BootOrder) || vm.BootOrder == "auto")
                        vm.BootOrder = "CD_HARDDISK_FLOPPY";
                }

                if (string.IsNullOrEmpty(vm.HdaFile) && diskMb > 0)
                {
                    string target = Path.Combine(Paths.VmDisksDir(vm.Id), "system.img");
                    AtomicFile.CreateSparse(target, (long)diskMb * 1024 * 1024);
                    vm.HdaFile = Paths.Relative(vm.Id, target);
                }

                if (string.IsNullOrEmpty(vm.HdaFile) && string.IsNullOrEmpty(vm.CdromFile) &&
                    string.IsNullOrEmpty(vm.FdaFile))
                {
                    throw new Hv86Exception("у ВМ нет ни диска, ни образа для загрузки");
                }

                VmStore.Save(vm);

                lock (_gate)
                {
                    _vms[vm.Id] = new VmRuntime
                    {
                        Def = vm,
                        NextRingUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(2000, vm.FtIntervalMs))
                    };
                }
            }
            catch
            {
                // не оставляем мусор от неудачного создания
                try { AtomicFile.MoveToTrash(Paths.VmDir(vm.Id), "broken-" + vm.Id); } catch { }
                throw;
            }

            Log.Info("VM", "создана ВМ " + vm.Id + " (" + vm.Name + "), память " + vm.MemoryMb + " МБ");

            Emit("vm.created", vm.ToView());
            return vm.ToView();
        }

        // --------------------------------------------------------------- запуск

        public async Task<object> StartAsync(string id, bool showConsole, string restorePath, int relayPort = 0,
            string leaseToken = null)
        {
            VmRuntime runtime = Get(id);

            // state v86 между версиями несовместим: чужим движком ВМ не поднять.
            // Честнее отказать и объяснить, чем уронить гостя на первом откате.
            if (VmUpgrade.NeedsMigration(runtime.Def))
                throw new Hv86Exception(VmUpgrade.Hint(runtime.Def));

            // аренда у свидетеля: без неё запускаться нельзя - иначе две машины
            // одновременно решат, что они главные (docs/replica-quorum.md).
            // Токен может прийти снаружи: реплика уже выиграла аренду, повторный
            // claim её же и не отдал бы.
            if (!string.IsNullOrEmpty(leaseToken))
            {
                lock (_gate)
                {
                    runtime.LeaseHeld = true;
                    runtime.LeaseToken = leaseToken;
                    runtime.LeaseExpiresUtc = DateTime.UtcNow.AddSeconds(LeaseTtlSec);
                    runtime.LeaseRenewUtc = DateTime.UtcNow.AddSeconds(LeaseRenewSec);
                    runtime.LeaseError = "";
                }

                Log.Info("FT", "аренда " + id + " принята от реплики");
            }
            else if (!string.IsNullOrEmpty(runtime.Def.LeaseUrl))
            {
                Dictionary<string, object> lease = WitnessClient.ClaimAsync(
                    runtime.Def.LeaseUrl, runtime.Def.LeaseToken, id, Environment.MachineName,
                    LeaseTtlSec, 5000).GetAwaiter().GetResult();

                if (!Json.GetBool(lease, "granted", false))
                {
                    string holder = Json.GetString(lease, "holder", "?");
                    long left = Json.GetLong(lease, "expires_in_sec", 0);

                    throw new Hv86Exception("ВМ уже ведёт другая машина (" + holder + ", аренда ещё " +
                                            left + " с). Запуск запрещён: иначе будет split-brain");
                }

                lock (_gate)
                {
                    runtime.LeaseHeld = true;
                    runtime.LeaseToken = Json.GetString(lease, "token", "");
                    runtime.LeaseExpiresUtc = DateTime.UtcNow.AddSeconds(Json.GetInt(lease, "ttl_sec", LeaseTtlSec));
                    runtime.LeaseRenewUtc = DateTime.UtcNow.AddSeconds(LeaseRenewSec);
                    runtime.LeaseError = "";
                }

                Log.Info("FT", "аренда " + id + " получена, TTL " + LeaseTtlSec + " с");
            }

            lock (_gate)
            {
                if (runtime.Proc != null && !runtime.Proc.HasExited && runtime.Def.State == "running")
                    return runtime.Def.ToView();
            }

            if (!string.IsNullOrEmpty(restorePath) && !File.Exists(restorePath))
            {
                Log.Warn("VM", "state-файл для восстановления не найден: " + restorePath);
                restorePath = null;
            }

            runtime.Def.State = "starting";
            runtime.Def.LastError = "";
            runtime.StartedUtc = DateTime.UtcNow;
            VmStore.Save(runtime.Def);
            EmitState(runtime);

            VmProcess process = null;

            // окно уже открыто для выключенной ВМ - грузим гостя в него,
            // второй процесс на тот же канал поднять нельзя
            VmProcess idle;
            lock (_gate)
            {
                idle = runtime.Idle;
                runtime.Idle = null;
            }

            if (idle != null)
            {
                if (idle.HasExited)
                {
                    idle.Dispose();
                }
                else
                {
                    try
                    {
                        await idle.Client.CallForResultAsync("vm.boot", null, 120000).ConfigureAwait(false);
                        process = idle;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("VM", "открытое окно не приняло загрузку, запускаю заново: " + ex.Message);

                        try { idle.Kill(); } catch { }
                        idle.Dispose();
                    }
                }
            }

            try
            {
                if (process == null)
                {
                    process = await VmProcess.LaunchAsync(runtime.Def, showConsole, restorePath,
                        (name, data) => OnVmEvent(id, name, data), false, relayPort).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                lock (_gate)
                {
                    runtime.Def.State = "failed";
                    runtime.Def.LastError = ex.Message;
                    try { VmStore.Save(runtime.Def); } catch { }
                }

                EmitState(runtime);
                throw;
            }

            lock (_gate)
            {
                runtime.Proc = process;
                runtime.StartedUtc = DateTime.UtcNow;
                runtime.LastBeatUtc = DateTime.UtcNow;
                runtime.Instructions = 0;
                runtime.Mips = 0;
                runtime.LastRestore = restorePath;
                runtime.NextRingUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(2000, runtime.Def.FtIntervalMs));
                runtime.Def.State = "running";
                runtime.Def.LastError = "";
                runtime.ConsecutiveFailures = 0;
                try { VmStore.Save(runtime.Def); } catch { }
            }

            EmitState(runtime);

            Emit("ft.event", Json.Obj(
                "id", id,
                "kind", restorePath == null ? "started" : "restored",
                "text", restorePath == null ? "ВМ запущена" : "ВМ восстановлена из чекпоинта",
                "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));

            // режим реплики: рядом поднимаем второй процесс, который будет
            // стоять на паузе с последним чекпоинтом
            if (runtime.Def.FtMode == "replica")
            {
                if (runtime.Def.IsHeadless)
                    Log.Info("FT", "реплика: headless-движок не умеет, для " + id + " остаются чекпоинты");
                else
                    StartReplica(runtime);
            }

            // удалённая реплика: новая эпоха, потому что образ диска с этого
            // момента - другой (ВМ стартовала с того, что на диске сейчас)
            if (runtime.Def.FtMode == "remote")
            {
                int epoch;

                lock (_gate)
                {
                    runtime.Def.ReplicaEpoch++;
                    epoch = runtime.Def.ReplicaEpoch;
                    try { VmStore.Save(runtime.Def); } catch { }
                }

                PushRemoteBase(runtime, epoch);
            }

            return runtime.Def.ToView();
        }

        public async Task<object> StopAsync(string id, bool keepRing)
        {
            VmRuntime runtime = Get(id);
            VmProcess process;
            VmProcess replica;
            VmProcess idle;

            lock (_gate)
            {
                process = runtime.Proc;
                runtime.Proc = null;

                // реплику гасим всегда: без основной она бесполезна, а место и
                // второй WebView2 занимает
                replica = runtime.Replica;
                runtime.Replica = null;
                runtime.ReplicaState = "";
                runtime.ReplicaStarting = false;

                // окно, открытое для выключенной ВМ, тоже закрываем
                idle = runtime.Idle;
                runtime.Idle = null;

                if (process == null || process.HasExited)
                {
                    runtime.Def.State = "stopped";
                    try { VmStore.Save(runtime.Def); } catch { }
                }
                else
                {
                    runtime.Def.State = "stopping";
                }
            }

            EmitState(runtime);

            if (replica != null && !replica.HasExited)
            {
                Log.Info("FT", "остановка реплики " + id);
                replica.Kill();
                await replica.WaitExitAsync(5000).ConfigureAwait(false);
            }

            replica?.Dispose();

            if (idle != null)
            {
                Log.Info("VM", "закрываю окно выключенной ВМ " + id);
                try { idle.Kill(); } catch { }
                idle.Dispose();
            }

            if (process != null && !process.HasExited)
            {
                await process.ShutdownAsync(30000).ConfigureAwait(false);

                if (!process.HasExited)
                {
                    Log.Warn("VM", "vmhost не завершился мягко, убиваю: " + id);
                    process.Kill();
                    await process.WaitExitAsync(5000).ConfigureAwait(false);
                }
            }

            process?.Dispose();

            lock (_gate)
            {
                runtime.Def.State = "stopped";
                runtime.Instructions = 0;
                runtime.Mips = 0;
                try { VmStore.Save(runtime.Def); } catch { }
            }

            if (!keepRing)
            {
                CheckpointStore.ClearRing(runtime.Def);
                lock (_gate) runtime.ConsecutiveFailures = 0;
            }

            // ВМ встала штатно - аренда больше не нужна, пусть реплика
            // подхватит сразу, а не через TTL
            ReleaseLease(runtime);

            EmitState(runtime);
            return runtime.Def.ToView();
        }

        public async Task<object> PauseAsync(string id)
        {
            VmRuntime runtime = Get(id);
            await Command(runtime, "vm.pause").ConfigureAwait(false);
            runtime.Def.State = "paused";
            VmStore.Save(runtime.Def);
            EmitState(runtime);
            return runtime.Def.ToView();
        }

        public async Task<object> ResumeAsync(string id)
        {
            VmRuntime runtime = Get(id);
            await Command(runtime, "vm.resume").ConfigureAwait(false);
            runtime.Def.State = "running";
            VmStore.Save(runtime.Def);
            EmitState(runtime);
            return runtime.Def.ToView();
        }

        public async Task<object> ResetAsync(string id)
        {
            VmRuntime runtime = Get(id);
            await Command(runtime, "vm.reset").ConfigureAwait(false);
            runtime.Def.State = "running";
            VmStore.Save(runtime.Def);
            EmitState(runtime);
            return runtime.Def.ToView();
        }

        /// <summary>
        /// Ввод в гостя: текст превращается в настоящие нажатия (v86 сам
        /// раскладывает символы в скан-коды). "\n" = Enter.
        /// Живёт в службе, потому что нужен и консоли (вставка), и скриптам
        /// (`hv86 --cli type`), и тестам.
        /// </summary>
        public async Task<object> KeysAsync(string id, string text, bool enter)
        {
            VmRuntime runtime = Get(id);

            if (runtime.Proc == null || runtime.Proc.HasExited)
                throw new Hv86Exception("ВМ не запущена: вводить некуда");

            string payload = text ?? "";
            if (enter) payload += "\n";

            payload = payload.Replace("\r\n", "\n").Replace('\r', '\n');

            if (payload.Length == 0) throw new Hv86Exception("пустой ввод");

            if (payload.Length > MaxKeys)
                throw new Hv86Exception("слишком длинный ввод: " + payload.Length +
                                        " символов, максимум " + MaxKeys);

            object response = await runtime.Proc.Client
                .CallForResultAsync("vm.key", Json.Obj("text", payload), 60000).ConfigureAwait(false);

            if (response == null) throw new Hv86Exception("движок не ответил на ввод");

            Log.Info("VM", "введено " + payload.Length + " символов в " + id);

            return Json.Obj("id", id, "chars", payload.Length);
        }

        /// <summary>
        /// Открыть консоль ВМ. `start=false` - как в Hyper-V: подключиться
        /// можно и к выключенной, окно покажет «ВМ выключена» и кнопку Start.
        /// Для web-движка это означает процесс без гостя (`--idle`), для
        /// headless - просто окно интерфейса: оно живёт отдельно от движка.
        /// </summary>
        public async Task<object> ConsoleAsync(string id, bool start)
        {
            VmRuntime runtime = Get(id);

            if (runtime.Proc != null && !runtime.Proc.HasExited)
            {
                await Command(runtime, "vm.show").ConfigureAwait(false);
                return runtime.Def.ToView();
            }

            // уже открытое окно без гостя: просто показываем
            VmProcess idle;
            lock (_gate) idle = runtime.Idle;

            if (idle != null && !idle.HasExited)
            {
                await idle.Client.CallForResultAsync("vm.show", null, 15000).ConfigureAwait(false);
                return runtime.Def.ToView();
            }

            if (!start)
            {
                if (runtime.Def.IsHeadless)
                {
                    // консоль headless - окно интерфейса, ему движок не нужен,
                    // оно само подключится, когда ВМ запустят
                    return runtime.Def.ToView();
                }

                VmProcess fresh = await VmProcess.LaunchAsync(runtime.Def, true, null,
                    (name, data) => OnVmEvent(id, name, data), false, 0, true).ConfigureAwait(false);

                lock (_gate) runtime.Idle = fresh;

                Log.Info("VM", "окно консоли открыто без запуска ВМ: " + id);
                return runtime.Def.ToView();
            }

            await StartAsync(id, true, null).ConfigureAwait(false);
            return runtime.Def.ToView();
        }

        public async Task<object> HideAsync(string id)
        {
            VmRuntime runtime = Get(id);
            if (runtime.Proc != null && !runtime.Proc.HasExited)
                await Command(runtime, "vm.hide").ConfigureAwait(false);
            return runtime.Def.ToView();
        }

        public async Task<object> DeleteAsync(string id)
        {
            VmRuntime runtime = Get(id);

            try { await StopAsync(id, false).ConfigureAwait(false); }
            catch (Exception ex) { Log.Warn("VM", "остановка перед удалением: " + ex.Message); }

            lock (_gate) _vms.Remove(id);

            VmStore.Delete(id, runtime.Def.Name);
            Emit("vm.deleted", Json.Obj("id", id));
            return Json.Obj("id", id);
        }

        // ----------------------------------------------------------- снапшоты

        public async Task<object> SnapshotAsync(string id, string name)
        {
            VmRuntime runtime = Get(id);

            if (runtime.Proc == null || runtime.Proc.HasExited)
                throw new Hv86Exception("ВМ не запущена: снапшот можно снять только с работающей ВМ");

            string temp = TempStatePath(id);
            Dictionary<string, object> result;

            try
            {
                object response = await runtime.Proc.Client.CallForResultAsync("vm.saveState",
                    Json.Obj("path", temp, "mode", "snapshot"), 300000).ConfigureAwait(false);
                result = response as Dictionary<string, object>;
            }
            catch (Exception ex)
            {
                TryDelete(temp);
                throw new Hv86Exception("не удалось снять состояние: " + ex.Message);
            }

            long instructions = Json.GetLong(result, "instructions", runtime.Instructions);
            long size = Json.GetLong(result, "size", 0);
            long ms = Json.GetLong(result, "ms", 0);

            Dictionary<string, object> snapshot = CheckpointStore.Create(runtime.Def, name, temp, instructions);

            Emit("vm.snapshot", Json.Obj(
                "id", id,
                "snapshot", snapshot,
                "size", size,
                "ms", ms));

            Emit("ft.event", Json.Obj(
                "id", id,
                "kind", "checkpoint",
                "text", "Снапшот «" + Json.GetString(snapshot, "name", "") + "» (" +
                        Math.Round(size / 1024.0 / 1024.0, 1) + " МБ, " + ms + " мс)",
                "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));

            return snapshot;
        }

        public List<object> Snapshots(string id)
        {
            Get(id);
            List<object> list = new List<object>();
            foreach (Dictionary<string, object> item in CheckpointStore.List(id)) list.Add(item);
            return list;
        }

        public Task<object> DeleteSnapshotAsync(string id, string snapshotId)
        {
            VmRuntime runtime = Get(id);
            CheckpointStore.Delete(runtime.Def, snapshotId);
            return Task.FromResult<object>(Json.Obj("id", id, "snapshot", snapshotId));
        }

        public async Task<object> RevertAsync(string id, string snapshotId)
        {
            VmRuntime runtime = Get(id);
            bool wasRunning = runtime.Proc != null && !runtime.Proc.HasExited;

            if (wasRunning) await StopAsync(id, false).ConfigureAwait(false);

            string state = CheckpointStore.PrepareRevert(runtime.Def, snapshotId);

            Emit("ft.event", Json.Obj(
                "id", id,
                "kind", "revert",
                "text", "Откат к снапшоту " + snapshotId,
                "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));

            return await StartAsync(id, false, state).ConfigureAwait(false);
        }

        // -------------------------------------------------------------- медиа

        public async Task<object> MountAsync(string id, string path, string kind)
        {
            VmRuntime runtime = Get(id);

            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new Hv86Exception("файл не найден: " + path);

            if (string.IsNullOrEmpty(kind)) kind = "cdrom";

            string ext = Path.GetExtension(path);
            if (string.IsNullOrEmpty(ext)) ext = ".iso";

            string target = Path.Combine(Paths.VmMediaDir(id),
                (kind == "fda" ? "floppy" : "install") + ext);

            File.Copy(path, target, true);

            lock (_gate)
            {
                if (kind == "fda")
                {
                    // своя дискета заменяет комплект: листать набор больше нечего
                    runtime.Def.FdaFile = Paths.Relative(id, target);
                    runtime.Def.FdaSet = new List<string>();
                    runtime.Def.FdaIndex = 0;
                }
                else runtime.Def.CdromFile = Paths.Relative(id, target);

                VmStore.Save(runtime.Def);
            }

            if (runtime.Proc != null && !runtime.Proc.HasExited)
            {
                await runtime.Proc.Client.CallForResultAsync("vm.mount",
                    Json.Obj("kind", kind, "path", target), 30000).ConfigureAwait(false);
            }

            Emit("vm.media", Json.Obj("id", id, "kind", kind, "file", Path.GetFileName(target)));
            return runtime.Def.ToView();
        }

        public async Task<object> EjectAsync(string id, string kind)
        {
            VmRuntime runtime = Get(id);
            if (string.IsNullOrEmpty(kind)) kind = "cdrom";

            if (runtime.Proc != null && !runtime.Proc.HasExited)
            {
                await runtime.Proc.Client.CallForResultAsync("vm.eject",
                    Json.Obj("kind", kind), 30000).ConfigureAwait(false);
            }

            lock (_gate)
            {
                if (kind == "fda") runtime.Def.FdaFile = null;
                else runtime.Def.CdromFile = null;

                VmStore.Save(runtime.Def);
            }

            Emit("vm.media", Json.Obj("id", id, "kind", kind, "file", ""));
            return runtime.Def.ToView();
        }

        /// <summary>
        /// Смена дискеты в комплекте. delta = +1/-1 листает по кругу,
        /// index >= 0 вставляет конкретную. Дискета меняется и у работающей
        /// ВМ: гость увидит новую вставку так же, как от настоящего привода.
        /// </summary>
        public async Task<object> FloppyAsync(string id, int delta, int index)
        {
            VmRuntime runtime = Get(id);

            string path;
            int position;
            int count;
            string fileName;

            lock (_gate)
            {
                VmDefinition def = runtime.Def;

                if (def.FdaSet == null || def.FdaSet.Count == 0)
                    throw new Hv86Exception("у этой ВМ нет набора дискет");

                count = def.FdaSet.Count;
                position = index >= 0 ? index : def.FdaIndex + (delta == 0 ? 1 : delta);

                // по кругу: после последней снова первая, как в 86box
                position = ((position % count) + count) % count;

                def.FdaIndex = position;
                def.FdaFile = def.FdaSet[position];

                VmStore.Save(def);

                path = Paths.Absolute(id, def.FdaFile);
                fileName = Path.GetFileName(def.FdaFile);
            }

            if (runtime.Proc != null && !runtime.Proc.HasExited)
            {
                await runtime.Proc.Client.CallForResultAsync("vm.floppy",
                    Json.Obj("path", path, "index", position), 30000).ConfigureAwait(false);
            }

            Emit("vm.floppy", Json.Obj("id", id, "file", fileName, "index", position, "count", count));
            return runtime.Def.ToView();
        }

        // ------------------------------------------------------------- настройки

        public Task<object> UpdateAsync(string id, Dictionary<string, object> parameters)
        {
            VmRuntime runtime = Get(id);
            bool running = runtime.Proc != null && !runtime.Proc.HasExited;

            lock (_gate)
            {
                VmDefinition def = runtime.Def;

                string name = Json.GetString(parameters, "name", null);
                if (!string.IsNullOrWhiteSpace(name)) def.Name = name.Trim();

                if (parameters.ContainsKey("memory_mb") && !running && def.State == "stopped")
                {
                    int memory = Json.GetInt(parameters, "memory_mb", def.MemoryMb);
                    def.MemoryMb = RoundPow2(Math.Max(16, Math.Min(1024, memory)));

                    // прежние снапшоты/чекпоинты сделаны при другом размере RAM
                    // и несовместимы - честнее убрать их сразу, чем падать при откате
                    CheckpointStore.ClearRing(def);
                }

                if (parameters.ContainsKey("vga_memory_mb") && !running)
                    def.VgaMemoryMb = Math.Max(2, Math.Min(32, Json.GetInt(parameters, "vga_memory_mb", def.VgaMemoryMb)));

                def.Autostart = Json.GetBool(parameters, "autostart", def.Autostart);

                string netMode = Json.GetString(parameters, "net_mode", null);
                if (netMode == "nat" || netMode == "relay") def.NetMode = netMode;

                // движок выбирается при старте: меняем только у остановленной ВМ
                string engine = Json.GetString(parameters, "engine", null);
                if ((engine == "web" || engine == "headless") && !running && def.State == "stopped")
                    def.Engine = engine;

                string mode = Json.GetString(parameters, "ft_mode", def.FtMode);
                if (mode == "off" || mode == "checkpoint" || mode == "replica" || mode == "remote")
                    def.FtMode = mode;

                // удалённая реплика: адрес и токен проверяем сразу, чтобы отказ
                // был при сохранении настроек, а не через 15 секунд в логе
                if (parameters.ContainsKey("replica_url"))
                {
                    string url = (Json.GetString(parameters, "replica_url", "") ?? "").Trim();

                    // сохранение прочих настроек у работающей ВМ не должно падать:
                    // проверяем только реальное изменение адреса
                    if (url != (def.ReplicaUrl ?? ""))
                    {
                        if (url.Length > 0)
                        {
                            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                                throw new Hv86Exception("адрес реплики должен начинаться с http:// или https://");

                            if (running || def.State != "stopped")
                                throw new Hv86Exception("удалённую реплику включают у остановленной ВМ: " +
                                                        "базовый образ отправляется при старте");
                        }

                        def.ReplicaUrl = url;
                    }
                }

                if (parameters.ContainsKey("replica_token"))
                    def.ReplicaToken = Json.GetString(parameters, "replica_token", "") ?? "";

                if (parameters.ContainsKey("lease_url"))
                {
                    string url = (Json.GetString(parameters, "lease_url", "") ?? "").Trim();

                    if (url != (def.LeaseUrl ?? ""))
                    {
                        if (url.Length > 0)
                        {
                            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                                throw new Hv86Exception("адрес свидетеля должен начинаться с http:// или https://");

                            if (running || def.State != "stopped")
                                throw new Hv86Exception("свидетеля подключают у остановленной ВМ: аренда берётся при старте");
                        }

                        def.LeaseUrl = url;
                    }
                }

                if (parameters.ContainsKey("lease_token"))
                    def.LeaseToken = Json.GetString(parameters, "lease_token", "") ?? "";

                if (parameters.ContainsKey("auto_promote"))
                    def.AutoPromote = Json.GetBool(parameters, "auto_promote", def.AutoPromote);

                if (def.LeaseUrl.Length > 0 && def.LeaseToken.Length == 0)
                    throw new Hv86Exception("для свидетеля нужен общий секрет (lease_token)");

                if (def.FtMode == "remote" && def.ReplicaUrl.Length > 0 && def.ReplicaToken.Length == 0)
                    throw new Hv86Exception("для удалённой реплики нужен общий токен: без него она не примет состояние");

                def.FtIntervalMs = Math.Max(2000, Math.Min(600000,
                    Json.GetInt(parameters, "ft_interval_ms", def.FtIntervalMs)));

                def.FtKeep = Math.Max(1, Math.Min(10, Json.GetInt(parameters, "ft_keep", def.FtKeep)));

                string restart = Json.GetString(parameters, "restart", def.Restart);
                if (restart == RestartPolicy.Always || restart == RestartPolicy.OnFailure || restart == RestartPolicy.Never)
                    def.Restart = restart;

                def.MaxAttempts = Math.Max(0, Math.Min(100,
                    Json.GetInt(parameters, "max_attempts", def.MaxAttempts)));

                def.RestoreOnCrash = Json.GetBool(parameters, "restore_on_crash", def.RestoreOnCrash);

                if (parameters.ContainsKey("forwards"))
                {
                    int dropped;
                    def.Forwards = ForwardRule.ParseList(Json.GetList(parameters, "forwards"), out dropped);
                    if (dropped > 0) Log.Warn("VM", "проброс: отброшено правил " + dropped);
                }

                ApplyShare(def, parameters);
                ApplyEgress(def, parameters);

                // шрифт текстового режима: меняется на лету, к гостю отношения
                // не имеет - только отрисовка на хосте
                if (parameters.ContainsKey("text_font"))
                {
                    string font = (Json.GetString(parameters, "text_font", "") ?? "").Trim();
                    def.TextFont = font.Length > 128 ? font.Substring(0, 128) : font;
                }

                if (parameters.ContainsKey("text_font_size"))
                    def.TextFontSize = Math.Max(0, Math.Min(48, Json.GetInt(parameters, "text_font_size", 0)));

                runtime.NextRingUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(2000, def.FtIntervalMs));

                try { VmStore.Save(def); } catch (Exception ex) { Log.Warn("VM", "сохранение настроек: " + ex.Message); }
            }

            // правила проброса применяются на лету: перезапускать ВМ ради этого
            // не нужно, слушатели живут в vmhost
            if (runtime.Proc != null && !runtime.Proc.HasExited)
            {
                // режим отказоустойчивости тоже применяем сразу: переключили на
                // replica - поднимаем второй процесс, ушли с него - гасим
                if (runtime.Def.FtMode == "replica" && !runtime.Def.IsHeadless)
                {
                    StartReplica(runtime);
                }
                else if (runtime.Def.FtMode != "replica")
                {
                    StopReplica(runtime, "режим отказоустойчивости изменён");
                }

                List<object> rules = new List<object>();
                foreach (ForwardRule rule in runtime.Def.Forwards) rules.Add(rule.ToDict());

               _ = Task.Run(async () =>
                {
                    try
                    {
                        await runtime.Proc.Client.CallForResultAsync("vm.forwards",
                            Json.Obj("rules", rules), 15000).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("VM", "применение проброса: " + ex.Message);
                    }
                });

                List<object> allow = new List<object>();
                foreach (string item in runtime.Def.EgressAllow) allow.Add(item);

                string egressMode = runtime.Def.EgressMode;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await runtime.Proc.Client.CallForResultAsync("vm.egress",
                            Json.Obj("mode", egressMode, "allow", allow), 15000).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("VM", "применение политики egress: " + ex.Message);
                    }
                });

                // шрифт текстового режима применяем сразу: перезапуск не нужен
                string font = runtime.Def.TextFont;
                int fontSize = runtime.Def.TextFontSize;

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await runtime.Proc.Client.CallForResultAsync("vm.font",
                            Json.Obj("font", font, "size", fontSize), 15000).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Warn("VM", "применение шрифта: " + ex.Message);
                    }
                });
            }

            Log.Info("VM", "настройки ВМ " + id + " обновлены");

            Emit("vm.updated", runtime.Def.ToView());
            EmitState(runtime);

            return Task.FromResult<object>(runtime.Def.ToView());
        }

        /// <summary>
        /// Переносит остановленную ВМ на встроенный движок v86: диски остаются,
        /// снапшоты и чекпоинты старой версии уходят в корзину (state между
        /// версиями несовместим). Окно ВМ должно быть закрыто: у открытого окна
        /// своя копия vm.json, и она бы вернула старую версию обратно.
        /// </summary>
        public Task<object> MigrateAsync(string id)
        {
            VmRuntime runtime = Get(id);

            lock (_gate)
            {
                if (runtime.Proc != null && !runtime.Proc.HasExited)
                    throw new Hv86Exception("остановите ВМ перед переносом на другой движок");

                if (runtime.Idle != null && !runtime.Idle.HasExited)
                    throw new Hv86Exception("закройте окно ВМ перед переносом на другой движок");

                if (!VmUpgrade.NeedsMigration(runtime.Def))
                    return Task.FromResult<object>(Json.Obj(
                        "migrated", false,
                        "version", runtime.Def.V86Version,
                        "vm", runtime.Def.ToView()));

                Dictionary<string, object> summary = VmUpgrade.Migrate(runtime.Def);

                VmStore.Save(runtime.Def);

                Emit("vm.updated", runtime.Def.ToView());
                EmitState(runtime);

                summary["migrated"] = true;
                summary["vm"] = runtime.Def.ToView();

                return Task.FromResult<object>(summary);
            }
        }

        /// <summary>
        /// Поднимает принятую реплику как обычную ВМ на этом хосте: раскладывает
        /// образ и последний state в каталог ВМ и запускает с восстановления.
        /// Порт релея берётся из реплики - адрес wisp://127.0.0.1:&lt;порт&gt; уже
        /// записан в state, и гость после переключения должен найти сеть там же.
        /// </summary>
        public async Task<object> PromoteReplicaAsync(string id, string leaseToken = null)
        {
            ReplicaStore store = ReplicaStore.Default();

            if (store.Read(id) == null)
                throw new Hv86Exception("на этом хосте нет реплики " + id);

            VmRuntime runtime;

            lock (_gate)
            {
                _vms.TryGetValue(id, out runtime);

                if (runtime != null &&
                    ((runtime.Proc != null && !runtime.Proc.HasExited) ||
                     (runtime.Idle != null && !runtime.Idle.HasExited)))
                    throw new Hv86Exception("ВМ " + id + " уже работает на этом хосте: сначала остановите её");
            }

            string statePath;
            Dictionary<string, object> staged = store.Promote(id, out statePath);
            VmDefinition def = staged["vm"] as VmDefinition;

            if (def == null) throw new Hv86Exception("реплика не разложилась в описание ВМ");

            lock (_gate)
            {
                if (runtime == null)
                {
                    runtime = new VmRuntime
                    {
                        Def = def,
                        NextRingUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(2000, def.FtIntervalMs))
                    };

                    _vms[id] = runtime;
                }
                else
                {
                    runtime.Def = def;
                    runtime.Instructions = 0;
                    runtime.Mips = 0;
                    runtime.LastRestore = null;
                }
            }

            int relayPort = Json.GetInt(staged, "relay_port", 0);

            Emit("vm.created", def.ToView());
            Emit("ft.event", Json.Obj(
                "id", id,
                "kind", "replica-promoted",
                "text", "Реплика поднята как основная ВМ (state " + Json.GetLong(staged, "seq", 0) +
                        ", эпоха " + Json.GetInt(staged, "epoch", 0) + ")",
                "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));

            try
            {
                return await StartAsync(id, true, statePath, relayPort, leaseToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // образ и state уже на месте: повторный запуск возможен обычным
                // способом, поэтому не притворяемся, что всё пропало
                lock (_gate)
                {
                    runtime.Def.State = "stopped";
                    runtime.Def.LastError = ex.Message;
                    try { VmStore.Save(runtime.Def); } catch { }
                }

                EmitState(runtime);

                throw new Hv86Exception("реплика разложена, но ВМ не запустилась: " + ex.Message +
                                        ". Диск и state на месте - запустите ВМ обычным способом");
            }
        }

        public Task<object> CloneAsync(string id, string name)
        {
            VmRuntime runtime = Get(id);

            if (runtime.Proc != null && !runtime.Proc.HasExited)
                throw new Hv86Exception("остановите ВМ перед клонированием");

            lock (_gate)
            {
                VmDefinition copy = VmStore.Clone(id, string.IsNullOrWhiteSpace(name)
                    ? runtime.Def.Name + " (clone)"
                    : name.Trim());

                _vms[copy.Id] = new VmRuntime
                {
                    Def = copy,
                    NextRingUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(2000, copy.FtIntervalMs))
                };

                Log.Info("VM", "клон " + id + " -> " + copy.Id);
                Emit("vm.created", copy.ToView());

                return Task.FromResult<object>(copy.ToView());
            }
        }

        public Task<object> ExportAsync(string id, string targetPath, bool includeSnapshots)
        {
            VmRuntime runtime = Get(id);

            if (runtime.Proc != null && !runtime.Proc.HasExited)
                throw new Hv86Exception("остановите ВМ перед экспортом");

            Dictionary<string, object> result = VmArchive.Export(runtime.Def, targetPath, includeSnapshots);
            Emit("vm.exported", Json.Obj("id", id, "path", Json.GetString(result, "path", "")));

            return Task.FromResult<object>(result);
        }

        public Task<object> ImportAsync(string archivePath)
        {
            string newId;
            VmDefinition vm = VmArchive.Import(archivePath, out newId);

            lock (_gate)
            {
                _vms[vm.Id] = new VmRuntime
                {
                    Def = vm,
                    NextRingUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(2000, vm.FtIntervalMs))
                };
            }

            Emit("vm.created", vm.ToView());
            return Task.FromResult<object>(vm.ToView());
        }

        // ------------------------------------------------------- сторож (FT)

        /// <summary>Вызывается таймером службы раз в 2 секунды.</summary>
        public void Tick()
        {
            if (_disposed) return;

            foreach (VmRuntime runtime in Snapshot())
            {
                try { TickOne(runtime); }
                catch (Exception ex) { Log.Exception("FT", "сторож для " + runtime.Def.Id, ex); }
            }
        }

        private void TickOne(VmRuntime runtime)
        {
            VmDefinition def = runtime.Def;

            // аренда: продлеваем и, если не успели, останавливаем ВМ. Это и есть
            // fencing - лучше простой, чем два хозяина (docs/replica-quorum.md)
            if (runtime.LeaseHeld)
            {
                if (DateTime.UtcNow > runtime.LeaseExpiresUtc)
                {
                    lock (_gate)
                    {
                        runtime.LeaseHeld = false;
                        runtime.LeaseError = "аренда истекла";
                    }

                    Log.Error("FT", "аренда " + def.Id + " истекла: останавливаю ВМ (fencing)");

                    Emit("ft.event", Json.Obj(
                        "id", def.Id,
                        "kind", "fenced",
                        "text", "Аренда у свидетеля истекла: ВМ остановлена, чтобы не было двух хозяев",
                        "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));

                    Task.Run(async delegate
                    {
                        try { await StopAsync(def.Id, true).ConfigureAwait(false); }
                        catch (Exception ex) { Log.Warn("FT", "остановка после fencing: " + ex.Message); }
                    });

                    return;
                }

                if (DateTime.UtcNow >= runtime.LeaseRenewUtc) RenewLease(runtime);
            }

            // окно выключенной ВМ: процесс живёт сам по себе, и если закрылся -
            // просто забываем его, ВМ всё равно не запущена
            if (runtime.Idle != null && runtime.Idle.HasExited)
            {
                try { runtime.Idle.Dispose(); } catch { }

                lock (_gate) runtime.Idle = null;
            }

            if (def.State == "stopped" || def.State == "failed" || def.State == "stopping") return;

            // запуск/восстановление может занять десятки секунд (WebView2 +
            // wasm + образ): процесс ещё не создан - это не падение
            if (def.State == "starting" || def.State == "recovering")
            {
                if (runtime.Restarting) return;
                if ((DateTime.UtcNow - runtime.StartedUtc).TotalSeconds < 120) return;
            }

            VmProcess process = runtime.Proc;

            if (process == null || process.HasExited)
            {
                HandleCrash(runtime, "процесс ВМ завершился (код " + (process == null ? -1 : process.ExitCode) + ")");
                return;
            }

            TimeSpan silence = DateTime.UtcNow - runtime.LastBeatUtc;

            if (def.State == "running" && silence.TotalSeconds > 10)
            {
                Log.Warn("FT", "ВМ " + def.Id + " молчит " + (int)silence.TotalSeconds + " с");
                process.Kill();
                HandleCrash(runtime, "нет heartbeat " + (int)silence.TotalSeconds + " с");
                return;
            }

            if (def.State != "running") return;

            // реплика: второй процесс должен быть жив и прогрет
            if (def.FtMode == "replica" && !def.IsHeadless) SuperviseReplica(runtime);

            if (def.FtMode == "off") return;
            if (runtime.RingBusy) return;
            if (DateTime.UtcNow < runtime.NextRingUtc) return;

            StartRing(runtime);
        }

        private void StartRing(VmRuntime runtime)
        {
            runtime.RingBusy = true;
            runtime.NextRingUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(2000, runtime.Def.FtIntervalMs));

            string id = runtime.Def.Id;
            string temp = TempStatePath(id);

            Task.Run(async () =>
            {
                try
                {
                    if (runtime.Proc == null || runtime.Proc.HasExited) return;

                    object response = await runtime.Proc.Client.CallForResultAsync("vm.saveState",
                        Json.Obj("path", temp, "mode", "ring"), 120000).ConfigureAwait(false);

                    Dictionary<string, object> result = response as Dictionary<string, object>;
                    long instructions = Json.GetLong(result, "instructions", runtime.Instructions);
                    long ms = Json.GetLong(result, "ms", 0);

                    string saved = CheckpointStore.SaveRing(runtime.Def, temp, instructions);

                    runtime.ConsecutiveFailures = 0;

                    Emit("ft.event", Json.Obj(
                        "id", id,
                        "kind", "ring",
                        "text", "Чекпоинт за " + ms + " мс",
                        "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));

                    // реплика: тот же чекпоинт сразу отдаём второму процессу,
                    // чтобы он отставал от основной на один интервал, а не на
                    // всё время с момента своего запуска
                    VmProcess replica = null;

                    if (runtime.Def.FtMode == "replica")
                        lock (_gate) replica = runtime.Replica;

                    if (replica != null && !replica.HasExited)
                        await PushReplicaStateAsync(runtime, replica, saved).ConfigureAwait(false);

                    // удалённая реплика: тот же файл уезжает по сети, но своей
                    // очередью - медленная сеть не должна тормозить чекпоинты
                    if (runtime.Def.FtMode == "remote")
                        PushRemoteState(runtime, saved);
                }
                catch (Exception ex)
                {
                    TryDelete(temp);
                    runtime.ConsecutiveFailures++;

                    Log.Warn("FT", "чекпоинт " + id + ": " + ex.Message);

                    // ВМ не отвечает на сохранение - не долбим её каждые 2 секунды
                    runtime.NextRingUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(5000, runtime.Def.FtIntervalMs * 2));
                }
                finally
                {
                    runtime.RingBusy = false;
                }
            });
        }

        // ------------------------------------------------------------- реплика

        /// <summary>
        /// Поднимает второй процесс той же ВМ. Он грузит последний чекпоинт и
        /// стоит на паузе: пока основная работает, реплика только принимает
        /// новые состояния, а CPU не крутит.
        /// </summary>
        private void StartReplica(VmRuntime runtime)
        {
            string id = runtime.Def.Id;

            lock (_gate)
            {
                if (runtime.ReplicaStarting) return;
                if (runtime.Replica != null && !runtime.Replica.HasExited) return;
                if (runtime.Proc == null || runtime.Proc.HasExited) return;

                runtime.ReplicaStarting = true;
                runtime.ReplicaState = "starting";
                runtime.ReplicaUtc = DateTime.UtcNow;
            }

            int relayPort = runtime.RelayPort;

            Task.Run(async () =>
            {
                VmProcess replica = null;

                try
                {
                    replica = await VmProcess.LaunchAsync(runtime.Def, false, null,
                        (name, data) => OnReplicaEvent(runtime, name, data), true, relayPort).ConfigureAwait(false);

                    bool keep;

                    lock (_gate)
                    {
                        // режим мог смениться, пока мы запускали процесс:
                        // тогда реплика не нужна и её надо погасить
                        keep = runtime.Def.FtMode == "replica" &&
                               runtime.Proc != null && !runtime.Proc.HasExited &&
                               (runtime.Def.State == "running" || runtime.Def.State == "paused");

                        if (keep) runtime.Replica = replica;
                    }

                    if (!keep)
                    {
                        // пока поднимали - ВМ остановили или сменили режим: реплика не нужна
                        Log.Info("FT", "реплика " + id + " не нужна: ВМ не в режиме реплики");
                        try { replica.Kill(); } catch { }
                        replica.Dispose();
                        return;
                    }

                    Emit("ft.event", Json.Obj(
                        "id", id,
                        "kind", "replica",
                        "text", "Реплика запущена (канал " + replica.PipeName + ")",
                        "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));

                    // реплика стартует «пустой»: сразу отдаём ей свежий чекпоинт,
                    // не дожидаясь следующего круга кольца
                    string ring = CheckpointStore.NewestRing(runtime.Def);

                    if (ring != null)
                        await PushReplicaStateAsync(runtime, replica, ring).ConfigureAwait(false);

                    string state;
                    lock (_gate) state = runtime.ReplicaState;

                    Log.Info("FT", "реплика " + id + ": " + (state == "warm" ? "прогрета" : "ждёт чекпоинт"));
                }
                catch (Exception ex)
                {
                    Log.Exception("FT", "реплика " + id, ex);

                    lock (_gate)
                    {
                        runtime.Replica = null;
                        runtime.ReplicaState = "";
                    }

                    if (replica != null)
                    {
                        try { replica.Kill(); } catch { }
                        try { replica.Dispose(); } catch { }
                    }

                    Emit("ft.event", Json.Obj(
                        "id", id,
                        "kind", "replica-failed",
                        "text", "Реплика не поднялась: " + ex.Message,
                        "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
                }
                finally
                {
                    lock (_gate) runtime.ReplicaStarting = false;
                }
            });
        }

        /// <summary>Отдаёт реплике состояние чекпоинта. true - она прогрета.</summary>
        private async Task<bool> PushReplicaStateAsync(VmRuntime runtime, VmProcess replica, string statePath)
        {
            if (replica == null || replica.HasExited) return false;

            try
            {
                await replica.Client.CallForResultAsync("vm.restoreState",
                    Json.Obj("path", statePath), 180000).ConfigureAwait(false);

                lock (_gate)
                {
                    runtime.ReplicaState = "warm";
                    runtime.ReplicaUtc = DateTime.UtcNow;
                }

                return true;
            }
            catch (Exception ex)
            {
                Log.Warn("FT", "реплика " + runtime.Def.Id + " не приняла состояние: " + ex.Message);

                lock (_gate) runtime.ReplicaState = "starting";

                return false;
            }
        }

        /// <summary>Гасит реплику (переключение режима, остановка ВМ).</summary>
        private void StopReplica(VmRuntime runtime, string reason)
        {
            VmProcess replica;

            lock (_gate)
            {
                replica = runtime.Replica;
                runtime.Replica = null;
                runtime.ReplicaState = "";
                runtime.ReplicaUtc = DateTime.UtcNow;
            }

            if (replica == null) return;

            Log.Info("FT", "реплика " + runtime.Def.Id + " гасится: " + reason);

            try { replica.Kill(); } catch { }
            replica.Dispose();

            Emit("ft.event", Json.Obj(
                "id", runtime.Def.Id,
                "kind", "replica-off",
                "text", "Реплика остановлена: " + reason,
                "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
        }

        /// <summary>Сторож реплики: упавшую поднимаем заново, живую не трогаем.</summary>
        private void SuperviseReplica(VmRuntime runtime)
        {
            VmProcess dead = null;
            bool restart;

            lock (_gate)
            {
                if (runtime.Replica != null && runtime.Replica.HasExited)
                {
                    dead = runtime.Replica;
                    runtime.Replica = null;
                    runtime.ReplicaState = "";
                    runtime.ReplicaUtc = DateTime.UtcNow;
                }

                restart = runtime.Replica == null && !runtime.ReplicaStarting &&
                          (DateTime.UtcNow - runtime.ReplicaUtc).TotalSeconds > 15;
            }

            if (dead != null)
            {
                Log.Warn("FT", "реплика " + runtime.Def.Id + " завершилась, поднимаю заново");
                try { dead.Dispose(); } catch { }

                Emit("ft.event", Json.Obj(
                    "id", runtime.Def.Id,
                    "kind", "replica-lost",
                    "text", "Реплика потеряна: поднимаю заново",
                    "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
            }

            if (restart) StartReplica(runtime);
        }

        /// <summary>
        /// Повышение реплики: единственный путь, которым ВМ переживает падение
        /// основной без холодной загрузки. Возвращает true, если ВМ продолжает
        /// работать уже на втором процессе.
        /// </summary>
        /// <summary>
        /// Отправляет базовый образ на удалённую реплику (новая эпоха). Образ
        /// уезжает целиком: state кумулятивен относительно образа на момент
        /// старта ВМ, поэтому реплике нужен именно он.
        /// </summary>
        private void PushRemoteBase(VmRuntime runtime, int epoch)
        {
            VmDefinition def = runtime.Def;

            if (string.IsNullOrEmpty(def.ReplicaUrl)) return;

            if (!def.HasHda)
            {
                Log.Warn("FT", "удалённая реплика " + def.Id + ": нет образа диска, реплицировать нечего");
                return;
            }

            string disk = def.HdaPath;
            string url = ReplicaClient.NormalizeUrl(def.ReplicaUrl);
            string token = def.ReplicaToken;
            string id = def.Id;

            lock (_gate)
            {
                runtime.RemoteState = "base";
                runtime.RemoteError = "";
                runtime.RemoteSeq = 0;
            }

            Log.Info("FT", "реплика " + id + ": отправляю образ эпохи " + epoch + " (" +
                           (new FileInfo(disk).Length / 1024 / 1024) + " МБ)");

            Task.Run(async () =>
            {
                try
                {
                    Dictionary<string, object> meta = await ReplicaClient.BeginAsync(url, token, def, disk, epoch,
                        1800000).ConfigureAwait(false);

                    lock (_gate)
                    {
                        // пока копировали, ВМ могли остановить и запустить снова:
                        // тогда актуальна уже другая эпоха, и этот ответ - старый
                        if (def.ReplicaEpoch != epoch) return;

                        runtime.RemoteState = "ok";
                        runtime.RemoteUtc = DateTime.UtcNow;
                    }

                    Emit("ft.event", Json.Obj(
                        "id", id,
                        "kind", "replica-base",
                        "text", "Реплика приняла образ эпохи " + Json.GetInt(meta, "epoch", epoch),
                        "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        runtime.RemoteState = "error";
                        runtime.RemoteError = ex.Message;
                    }

                    Log.Warn("FT", "реплика " + id + " не приняла образ: " + ex.Message);
                }
            });
        }

        /// <summary>
        /// Отправляет state на удалённую реплику. Не блокирует чекпоинты: если
        /// прошлая отправка ещё идёт, этот чекпоинт пропускаем - лучше отстать
        /// на два интервала, чем накопить очередь из state-файлов.
        /// </summary>
        private void PushRemoteState(VmRuntime runtime, string statePath)
        {
            VmDefinition def = runtime.Def;

            if (string.IsNullOrEmpty(def.ReplicaUrl)) return;

            bool busy;

            lock (_gate) busy = runtime.RemoteBusy;

            if (busy)
            {
                Log.Warn("FT", "реплика " + def.Id + ": прошлая отправка ещё идёт, чекпоинт пропущен");
                return;
            }

            long seq = DateTime.UtcNow.Ticks;
            string url = ReplicaClient.NormalizeUrl(def.ReplicaUrl);
            string token = def.ReplicaToken;
            int epoch = def.ReplicaEpoch;
            string id = def.Id;

            lock (_gate)
            {
                runtime.RemoteBusy = true;
                runtime.RemoteState = "pushing";
            }

            Task.Run(async () =>
            {
                try
                {
                    Dictionary<string, object> result = await ReplicaClient.PutStateAsync(url, token, id, epoch,
                        seq, statePath, 600000, runtime.RelayPort, runtime.LeaseToken).ConfigureAwait(false);

                    lock (_gate)
                    {
                        if (Json.GetBool(result, "duplicate", false))
                        {
                            runtime.RemoteState = "ok";
                            return;
                        }

                        runtime.RemoteState = "ok";
                        runtime.RemoteUtc = DateTime.UtcNow;
                        runtime.RemoteSeq = seq;
                        runtime.RemoteError = "";
                    }
                }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        runtime.RemoteState = "error";
                        runtime.RemoteError = ex.Message;
                    }

                    Log.Warn("FT", "реплика " + id + ": " + ex.Message);
                }
                finally
                {
                    lock (_gate) runtime.RemoteBusy = false;
                }
            });
        }

        /// <summary>
        /// Продление аренды. Отправляем в фоне: сеть до свидетеля не должна
        /// тормозить сторож. Неудача не смертельна - до истечения есть ещё
        /// попытки, а факт истечения остановит ВМ.
        /// </summary>
        private void RenewLease(VmRuntime runtime)
        {
            string url = runtime.Def.LeaseUrl;
            string secret = runtime.Def.LeaseToken;
            string id = runtime.Def.Id;
            string token;

            lock (_gate)
            {
                token = runtime.LeaseToken;
                runtime.LeaseRenewUtc = DateTime.UtcNow.AddSeconds(LeaseRenewSec);
            }

            if (string.IsNullOrEmpty(token)) return;

            Task.Run(async delegate
            {
                try
                {
                    await WitnessClient.RenewAsync(url, secret, id, token, LeaseTtlSec, 5000).ConfigureAwait(false);

                    lock (_gate)
                    {
                        runtime.LeaseExpiresUtc = DateTime.UtcNow.AddSeconds(LeaseTtlSec);
                        runtime.LeaseError = "";
                    }
                }
                catch (Exception ex)
                {
                    lock (_gate) runtime.LeaseError = ex.Message;

                    Log.Warn("FT", "аренда " + id + " не продлена: " + ex.Message);
                }
            });
        }

        /// <summary>Отпустить аренду при штатной остановке (best effort).</summary>
        private void ReleaseLease(VmRuntime runtime)
        {
            string url = runtime.Def.LeaseUrl;
            string secret = runtime.Def.LeaseToken;
            string id = runtime.Def.Id;
            string token;
            bool held;

            lock (_gate)
            {
                held = runtime.LeaseHeld;
                token = runtime.LeaseToken;

                runtime.LeaseHeld = false;
                runtime.LeaseToken = "";
            }

            if (!held || string.IsNullOrEmpty(token)) return;

            Task.Run(async delegate
            {
                try
                {
                    await WitnessClient.ReleaseAsync(url, secret, id, token, 5000).ConfigureAwait(false);
                    Log.Info("FT", "аренда " + id + " отпущена");
                }
                catch (Exception ex)
                {
                    Log.Warn("FT", "аренда " + id + " не отпущена: " + ex.Message);
                }
            });
        }

        private async Task<bool> TryPromoteAsync(VmRuntime runtime, string reason)
        {
            VmProcess replica;
            string state;

            lock (_gate)
            {
                replica = runtime.Replica;
                state = runtime.ReplicaState;
            }

            if (replica == null || replica.HasExited || state != "warm") return false;

            string id = runtime.Def.Id;

            Log.Warn("FT", "падение основной ВМ " + id + ": повышаю реплику");

            try
            {
                await replica.Client.CallForResultAsync("vm.promote", null, 60000).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Error("FT", "реплика " + id + " не повысилась: " + ex.Message);

                lock (_gate)
                {
                    runtime.Replica = null;
                    runtime.ReplicaState = "";
                    runtime.ReplicaUtc = DateTime.UtcNow;
                }

                try { replica.Kill(); } catch { }
                try { replica.Dispose(); } catch { }

                return false;
            }

            lock (_gate)
            {
                runtime.Replica = null;
                runtime.ReplicaState = "";
                runtime.ReplicaUtc = DateTime.UtcNow;

                runtime.Proc = replica;
                runtime.StartedUtc = DateTime.UtcNow;
                runtime.LastBeatUtc = DateTime.UtcNow;
                runtime.Instructions = 0;
                runtime.Mips = 0;
                runtime.LastRestore = null;
                runtime.NextRingUtc = DateTime.UtcNow.AddMilliseconds(Math.Max(2000, runtime.Def.FtIntervalMs));

                runtime.Def.Restarts++;
                runtime.Def.State = "running";

                // причина падения остаётся в ft.event, а не в last_error:
                // ВМ-то работает, красной строки в UI быть не должно
                runtime.Def.LastError = "";

                try { VmStore.Save(runtime.Def); } catch { }
            }

            EmitState(runtime);
            Emit("ft.event", Json.Obj(
                "id", id,
                "kind", "promoted",
                "text", "Реплика повышена до основной: " + reason,
                "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));

            // на замену сразу поднимаем новую реплику
            StartReplica(runtime);

            return true;
        }

        /// <summary>События реплики в общий поток не пускаем: у неё свой heartbeat.</summary>
        private void OnReplicaEvent(VmRuntime runtime, string name, Dictionary<string, object> data)
        {
            if (name == "vm.state")
                Log.Debug("FT", "реплика " + runtime.Def.Id + ": " + Json.GetString(data, "state", ""));
        }

        private void HandleCrash(VmRuntime runtime, string reason)
        {
            lock (_gate)
            {
                if (runtime.Restarting) return;
                runtime.Restarting = true;
            }

            string id = runtime.Def.Id;

            Task.Run(async () =>
            {
                VmDefinition def = runtime.Def;
                VmProcess old = runtime.Proc;
                runtime.Proc = null;

                try
                {
                    old?.Dispose();

                    // 1) есть прогретая реплика - не перезапускаем ВМ с
                    // чекпоинта, а повышаем второй процесс: гость продолжает
                    // работу с того места, где был последний чекпоинт
                    if (await TryPromoteAsync(runtime, reason).ConfigureAwait(false)) return;

                    lock (_gate)
                    {
                        def.State = "recovering";
                        def.LastError = reason;
                        try { VmStore.Save(def); } catch { }
                    }

                    EmitState(runtime);
                    Emit("ft.event", Json.Obj(
                        "id", id,
                        "kind", "crash",
                        "text", "Падение: " + reason,
                        "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));

                    bool allowed = def.Restart != RestartPolicy.Never;
                    if (allowed && def.MaxAttempts > 0 && def.Restarts >= def.MaxAttempts)
                    {
                        allowed = false;
                        reason = "исчерпан лимит попыток (" + def.MaxAttempts + ")";
                    }

                    if (!allowed)
                    {
                        lock (_gate)
                        {
                            def.State = "failed";
                            def.LastError = reason;
                            try { VmStore.Save(def); } catch { }
                        }

                        EmitState(runtime);
                        Emit("ft.event", Json.Obj(
                            "id", id, "kind", "gave-up", "text", "Автоперезапуск отключён: " + reason,
                            "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));
                        return;
                    }

                    string restore = def.RestoreOnCrash ? CheckpointStore.NewestRing(def) : null;

                    lock (_gate)
                    {
                        def.Restarts++;
                        try { VmStore.Save(def); } catch { }
                    }

                    await Task.Delay(500).ConfigureAwait(false);

                    Emit("ft.event", Json.Obj(
                        "id", id,
                        "kind", "restart",
                        "text", restore == null
                            ? "Перезапуск (без чекпоинта - холодная загрузка)"
                            : "Восстановление из чекпоинта " + Path.GetFileName(restore),
                        "utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture)));

                    await StartAsync(id, false, restore).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Exception("FT", "автоперезапуск " + id, ex);

                    lock (_gate)
                    {
                        def.State = "failed";
                        def.LastError = "автоперезапуск не удался: " + ex.Message;
                        try { VmStore.Save(def); } catch { }
                    }

                    EmitState(runtime);
                }
                finally
                {
                    runtime.Restarting = false;
                }
            });
        }

        /// <summary>Диагностика и тесты: имитирует падение vmhost (watchdog должен поднять ВМ).</summary>
        public void KillHost(string id)
        {
            VmRuntime runtime = Get(id);
            VmProcess process;
            lock (_gate) process = runtime.Proc;

            Log.Warn("FT", "принудительное убийство vmhost " + id);
            process?.Kill();
        }

        // ------------------------------------------------------------ служебное

        public void StopAll()
        {
            foreach (VmRuntime runtime in Snapshot())
            {
                try
                {
                    VmProcess idle;
                    // реплику гасим всегда, даже если основная уже мертва
                    VmProcess replica;
                    lock (_gate)
                    {
                        replica = runtime.Replica;
                        runtime.Replica = null;
                        runtime.ReplicaState = "";

                        idle = runtime.Idle;
                        runtime.Idle = null;
                    }

                    if (idle != null)
                    {
                        idle.Kill();
                        idle.Dispose();
                    }

                    if (replica != null)
                    {
                        replica.Kill();
                        replica.Dispose();
                    }

                    if (runtime.Proc == null || runtime.Proc.HasExited) continue;

                    // при остановке службы диски сохраняем: это не аварийная ситуация
                    runtime.Proc.ShutdownAsync(20000).GetAwaiter().GetResult();
                    runtime.Proc.Kill();
                    runtime.Proc.Dispose();
                    runtime.Proc = null;

                    runtime.Def.State = "stopped";
                    VmStore.Save(runtime.Def);
                    CheckpointStore.ClearRing(runtime.Def);
                }
                catch (Exception ex)
                {
                    Log.Warn("VM", "остановка при выходе: " + ex.Message);
                }
            }
        }

        private async Task Command(VmRuntime runtime, string method)
        {
            if (runtime.Proc == null || runtime.Proc.HasExited)
                throw new Hv86Exception("ВМ не запущена");

            object response = await runtime.Proc.Client.CallForResultAsync(method, null, 15000).ConfigureAwait(false);
            if (response == null) throw new Hv86Exception("нет ответа на " + method);
        }

        private void OnVmEvent(string id, string name, Dictionary<string, object> data)
        {
            if (name == "vm.metrics")
            {
                VmRuntime runtime;
                lock (_gate) _vms.TryGetValue(id, out runtime);

                if (runtime != null)
                {
                    runtime.LastBeatUtc = DateTime.UtcNow;
                    runtime.Instructions = Json.GetLong(data, "instructions", runtime.Instructions);
                    runtime.Mips = Json.GetInt(data, "mips", runtime.Mips);
                    runtime.RelayPort = Json.GetInt(data, "relay_port", runtime.RelayPort);
                    runtime.RelayClients = Json.GetInt(data, "relay_clients", runtime.RelayClients);
                }
            }

            if (data == null) data = new Dictionary<string, object>();
            data["id"] = id;

            Emit(name, data);
        }

        private void EmitState(VmRuntime runtime)
        {
            Emit("vm.state", Json.Obj(
                "id", runtime.Def.Id,
                "state", runtime.Def.State,
                "error", runtime.Def.LastError,
                "restarts", runtime.Def.Restarts));
        }

        private void Emit(string name, object data)
        {
            Action<string, object> handler = Event;
            if (handler == null) return;

            try { handler(name, data); }
            catch (Exception ex) { Log.Warn("VM", "событие " + name + ": " + ex.Message); }
        }

        private VmRuntime Get(string id)
        {
            if (string.IsNullOrEmpty(id)) throw new Hv86Exception("не указан id ВМ");

            VmRuntime runtime;
            lock (_gate)
            {
                if (!_vms.TryGetValue(id, out runtime)) runtime = null;
            }

            if (runtime == null) throw new Hv86Exception("ВМ не найдена: " + id);
            return runtime;
        }

        private List<VmRuntime> Snapshot()
        {
            lock (_gate) return new List<VmRuntime>(_vms.Values);
        }

        private string TempStatePath(string id)
        {
            string dir = Path.Combine(Paths.VmDir(id), "temp");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "state-" + Guid.NewGuid().ToString("N").Substring(0, 10) + ".bin");
        }

        /// <summary>Общая папка: путь проверяем сразу, чтобы ошибка была в UI, а не при старте.</summary>
        private static void ApplyShare(VmDefinition vm, Dictionary<string, object> parameters)
        {
            if (!parameters.ContainsKey("share_path")) return;

            string path = (Json.GetString(parameters, "share_path", "") ?? "").Trim();

            if (path.Length == 0)
            {
                vm.SharePath = null;
                return;
            }

            string full;

            try { full = Path.GetFullPath(path); }
            catch (Exception ex) { throw new Hv86Exception("некорректный путь общей папки: " + ex.Message); }

            if (!Directory.Exists(full))
                throw new Hv86Exception("общая папка не найдена: " + full);

            vm.SharePath = full;
            vm.ShareTag = "host9p";
            vm.ShareReadOnly = Json.GetBool(parameters, "share_readonly", vm.ShareReadOnly);
        }

        private static void ApplyEgress(VmDefinition vm, Dictionary<string, object> parameters)
        {
            if (parameters.ContainsKey("egress_mode"))
                vm.EgressMode = EgressPolicy.NormalizeMode(Json.GetString(parameters, "egress_mode", vm.EgressMode));

            if (parameters.ContainsKey("egress_allow"))
            {
                List<object> raw = Json.GetList(parameters, "egress_allow");
                List<string> items = new List<string>();

                if (raw != null)
                    foreach (object item in raw)
                        if (item is string text) items.Add(text);

                int dropped;
                List<EgressRule> rules = EgressPolicy.ParseRules(items, out dropped);

                vm.EgressAllow = new List<string>();
                foreach (EgressRule rule in rules) vm.EgressAllow.Add(rule.Text);

                if (dropped > 0) Log.Warn("VM", "egress: отброшено правил " + dropped);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (!string.IsNullOrEmpty(path) && File.Exists(path)) File.Delete(path); } catch { }
        }

        private static int RoundPow2(int value)
        {
            int result = 16;
            while (result < value && result < 1024) result *= 2;
            return result;
        }

        /// <summary>
        /// Расширение, под которым образ ляжет в каталог ВМ. Каталоги отдают
        /// файлы через dl.php?id=... - расширение в пути ничего не значит,
        /// поэтому ориентируемся на сигнатуру.
        /// </summary>
        private static string ImageExtension(string file)
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();

            if (ext != ".img" && ext != ".iso" && ext != ".ima" && ext != ".flp" &&
                ext != ".vfd" && ext != ".dsk" && ext != ".raw" && ext != ".bin" && ext != ".hdd")
                ext = ImagePack.Detect(file) == ImagePack.KindIso ? ".iso" : ".img";

            return ext;
        }

        public void Dispose()
        {
            _disposed = true;
            try { StopAll(); } catch { }
        }
    }
}