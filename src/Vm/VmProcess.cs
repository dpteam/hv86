using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>
    /// Один процесс ВМ: запуск, подключение к его каналу, остановка.
    ///
    /// Два движка с одним протоколом:
    ///   web      - `hv86.exe --vmhost` (WebView2 + v86, картинка и мышь);
    ///   headless - `node engine/vmhost.mjs` (v86 в Node, только VGA-текст,
    ///              зато работает в сессии 0 - без входа пользователя).
    ///
    /// Процесс отдельный, а не поток службы, ровно из-за отказоустойчивости:
    /// упавший/зависший хост можно убить и поднять заново, не трогая
    /// остальные ВМ и саму службу.
    /// </summary>
    public sealed class VmProcess : IDisposable
    {
        private static readonly object JobGate = new object();
        private static JobObject _job;

        public string VmId { get; private set; }
        public Process Process { get; private set; }
        public PipeClient Client { get; private set; }
        public DateTime LastBeatUtc = DateTime.UtcNow;
        public long Instructions;
        public int Mips;

        private VmProcess() { }

        private static JobObject SharedJob
        {
            get
            {
                lock (JobGate)
                {
                    if (_job == null) _job = new JobObject("hv86-vmhosts");
                    return _job;
                }
            }
        }

        // --------------------------------------------------------------- запуск

        public static async Task<VmProcess> LaunchAsync(VmDefinition vm, bool showConsole, string restorePath,
            Action<string, Dictionary<string, object>> onEvent)
        {
            if (vm == null) throw new Hv86Exception("ВМ не найдена");

            Process process;
            string arguments;
            string workingDir;

            if (vm.IsHeadless)
            {
                if (showConsole)
                    Log.Info("VMHOST", "headless-движок: окно консоли создаёт интерфейс, не процесс ВМ");

                process = StartHeadless(vm, restorePath, out arguments);
                workingDir = Paths.EngineDir;
            }
            else
            {
                arguments = SelfHost.Arguments("--vmhost " + vm.Id + (showConsole ? " --console" : " --hidden"));

                if (!string.IsNullOrEmpty(restorePath))
                    arguments += " --restore \"" + restorePath + "\"";

                string error;
                process = SessionLauncher.Start(SelfHost.FileName, arguments, out error);

                if (process == null)
                    throw new Hv86Exception("не удалось запустить процесс ВМ: " + error);

                workingDir = "";
            }

            Log.Info("VMHOST", "запуск (" + vm.Engine + "): " + arguments + " (" + SessionLauncher.Describe() + ")");

            VmProcess host = new VmProcess();
            host.VmId = vm.Id;
            host.Process = process;

            SharedJob.Assign(process);

            await host.ConnectAsync(onEvent).ConfigureAwait(false);
            return host;
        }

        /// <summary>Собирает конфиг для Node-движка и стартует его.</summary>
        private static Process StartHeadless(VmDefinition vm, string restorePath, out string arguments)
        {
            string node = NodeRuntime.Executable;
            if (string.IsNullOrEmpty(node))
                throw new Hv86Exception("headless-движок недоступен: " + NodeRuntime.Error);

            string engineDir = Paths.EngineDir;

            if (!File.Exists(Path.Combine(engineDir, "vmhost.mjs")))
                throw new Hv86Exception("не найден engine\\vmhost.mjs рядом с hv86");

            if (!Directory.Exists(Path.Combine(engineDir, "node_modules", "v86")))
                throw new Hv86Exception("в engine\\ нет пакета v86: выполните tools\\fetch-engine.ps1");

            string assetsError;
            string assets = V86Assets.Ensure(out assetsError);
            if (assets == null)
                throw new Hv86Exception("не удалось подготовить ассеты v86: " + assetsError);

            string tempDir = Path.Combine(Paths.VmDir(vm.Id), "temp");
            Directory.CreateDirectory(tempDir);

            string configPath = Path.Combine(tempDir, "boot-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".json");

            Dictionary<string, object> config = Json.Obj(
                "id", vm.Id,
                "name", vm.Name,
                "memory", (long)vm.MemoryMb * 1024 * 1024,
                "vgaMemory", (long)vm.VgaMemoryMb * 1024 * 1024,
                "bios", Path.Combine(assets, "seabios.bin"),
                "vgaBios", Path.Combine(assets, "vgabios.bin"),
                "wasm", Path.Combine(engineDir, "node_modules", "v86", "build", "v86.wasm"),
                "bootOrder", VmDefinition.BootOrderValue(vm.BootOrder),
                "netType", string.IsNullOrEmpty(vm.NetType) ? "virtio" : vm.NetType,
                "tempDir", tempDir);

            if (vm.HasHda) config["hda"] = vm.HdaPath;
            if (vm.HasCdrom) config["cdrom"] = vm.CdromPath;
            if (vm.HasFda) config["fda"] = vm.FdaPath;

            if (!string.IsNullOrEmpty(restorePath) && File.Exists(restorePath))
                config["initialState"] = restorePath;

            Json.WriteFile(configPath, config);

            arguments = "\"" + Path.Combine(engineDir, "vmhost.mjs") + "\" --config \"" + configPath + "\"";

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = node,
                Arguments = arguments,
                WorkingDirectory = engineDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            Process process = Process.Start(psi);
            if (process == null) throw new Hv86Exception("node не запустился");

            string prefix = "node/" + vm.Id;

            process.OutputDataReceived += delegate (object s, DataReceivedEventArgs e)
            {
                if (!string.IsNullOrEmpty(e.Data)) Log.Info(prefix, e.Data);
            };

            process.ErrorDataReceived += delegate (object s, DataReceivedEventArgs e)
            {
                if (!string.IsNullOrEmpty(e.Data)) Log.Error(prefix, e.Data);
            };

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            return process;
        }

        // ------------------------------------------------------------ канал

        private async Task ConnectAsync(Action<string, Dictionary<string, object>> onEvent)
        {
            PipeClient client = new PipeClient(PipeNames.VmHost(VmId));
            Client = client;

            client.EventReceived += delegate (string name, Dictionary<string, object> data)
            {
                LastBeatUtc = DateTime.UtcNow;

                if (name == "vm.metrics")
                {
                    Instructions = Json.GetLong(data, "instructions", Instructions);
                    Mips = Json.GetInt(data, "mips", Mips);
                }

                try { onEvent?.Invoke(name, data); }
                catch (Exception ex) { Log.Warn("VMHOST", "обработчик события: " + ex.Message); }
            };

            if (!await client.EnsureConnectedAsync(30000).ConfigureAwait(false))
            {
                try { if (!Process.HasExited) Process.Kill(true); } catch { }
                client.Dispose();

                throw new Hv86Exception("процесс ВМ не открыл канал управления (см. лог " + VmId + ")");
            }

            DateTime deadline = DateTime.UtcNow.AddSeconds(30);

            while (DateTime.UtcNow < deadline)
            {
                if (Process.HasExited)
                    throw new Hv86Exception("процесс ВМ завершился при старте (код " + Process.ExitCode + ")");

                try
                {
                    object result = await client.CallForResultAsync("vm.status", null, 3000).ConfigureAwait(false);
                    Dictionary<string, object> status = result as Dictionary<string, object>;
                    string state = Json.GetString(status, "state", "");

                    if (state == "running" || state == "paused") return;
                    if (state == "failed")
                        throw new Hv86Exception(Json.GetString(status, "error", "эмулятор не запустился"));
                }
                catch (Hv86Exception) { throw; }
                catch (Exception ex) { Log.Debug("VMHOST", "опроса статуса: " + ex.Message); }

                await Task.Delay(300).ConfigureAwait(false);
            }

            try { Process.Kill(true); } catch { }
            throw new Hv86Exception("ВМ не вышла в состояние running за 30 секунд");
        }

        // ------------------------------------------------------------ остановка

        /// <summary>Мягкое завершение: хост сохраняет диск и выходит.</summary>
        public async Task<bool> ShutdownAsync(int timeoutMs)
        {
            if (Process == null) return true;

            try
            {
                if (!Process.HasExited && Client != null)
                    await Client.CallForResultAsync("vm.shutdown", null, Math.Min(timeoutMs, 60000))
                        .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("VMHOST", "shutdown: " + ex.Message);
            }

            return await WaitExitAsync(timeoutMs).ConfigureAwait(false);
        }

        public async Task<bool> WaitExitAsync(int timeoutMs)
        {
            try
            {
                if (Process == null || Process.HasExited) return true;

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(500, timeoutMs));

                while (DateTime.UtcNow < deadline)
                {
                    if (Process.HasExited) return true;
                    await Task.Delay(200).ConfigureAwait(false);
                }

                return Process.HasExited;
            }
            catch
            {
                return true;
            }
        }

        public void Kill()
        {
            try
            {
                if (Process != null && !Process.HasExited) Process.Kill(true);
            }
            catch (Exception ex)
            {
                Log.Warn("VMHOST", "kill: " + ex.Message);
            }
        }

        public bool HasExited
        {
            get { try { return Process == null || Process.HasExited; } catch { return true; } }
        }

        public int ExitCode
        {
            get { try { return Process == null ? -1 : Process.ExitCode; } catch { return -1; } }
        }

        public void Dispose()
        {
            try { Client?.Dispose(); } catch { }
            try { Process?.Dispose(); } catch { }
            Client = null;
            Process = null;
        }
    }
}