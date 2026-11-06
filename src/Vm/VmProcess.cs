using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>
    /// Один процесс vmhost: запуск, подключение к его каналу, остановка.
    ///
    /// Процесс отдельный, а не поток службы, ровно из-за отказоустойчивости:
    /// упавший/зависший vmhost можно убить и поднять заново, не трогая
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

        /// <summary>
        /// Поднимает vmhost и ждёт, пока он откроет свой канал.
        /// Бросает Hv86Exception с человеческим текстом, если не вышло.
        /// </summary>
        public static async Task<VmProcess> LaunchAsync(VmDefinition vm, bool showConsole, string restorePath,
            Action<string, Dictionary<string, object>> onEvent)
        {
            if (vm == null) throw new Hv86Exception("ВМ не найдена");

            string arguments = SelfHost.Arguments("--vmhost " + vm.Id + (showConsole ? " --console" : " --hidden"));

            if (!string.IsNullOrEmpty(restorePath))
                arguments += " --restore \"" + restorePath + "\"";

            Log.Info("VMHOST", "запуск: " + SelfHost.FileName + " " + arguments + " (" + SessionLauncher.Describe() + ")");

            string error;
            Process process = SessionLauncher.Start(SelfHost.FileName, arguments, out error);

            if (process == null)
                throw new Hv86Exception("не удалось запустить процесс ВМ: " + error);

            VmProcess host = new VmProcess();
            host.VmId = vm.Id;
            host.Process = process;

            SharedJob.Assign(process);

            PipeClient client = new PipeClient(PipeNames.VmHost(vm.Id));
            host.Client = client;

            client.EventReceived += delegate (string name, Dictionary<string, object> data)
            {
                host.LastBeatUtc = DateTime.UtcNow;

                if (name == "vm.metrics")
                {
                    host.Instructions = Json.GetLong(data, "instructions", host.Instructions);
                    host.Mips = Json.GetInt(data, "mips", host.Mips);
                }

                try { onEvent?.Invoke(name, data); }
                catch (Exception ex) { Log.Warn("VMHOST", "обработчик события: " + ex.Message); }
            };

            if (!await client.EnsureConnectedAsync(20000).ConfigureAwait(false))
            {
                try { if (!process.HasExited) process.Kill(true); } catch { }
                client.Dispose();

                throw new Hv86Exception("процесс ВМ не открыл канал управления (см. лог vm)");
            }

            // ждём, пока эмулятор действительно начнёт работать
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);

            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                    throw new Hv86Exception("процесс ВМ завершился при старте (код " + process.ExitCode + ")");

                try
                {
                    object result = await client.CallForResultAsync("vm.status", null, 3000).ConfigureAwait(false);
                    Dictionary<string, object> status = result as Dictionary<string, object>;
                    string state = Json.GetString(status, "state", "");

                    if (state == "running" || state == "paused") return host;
                    if (state == "failed")
                        throw new Hv86Exception(Json.GetString(status, "error", "эмулятор не запустился"));
                }
                catch (Hv86Exception) { throw; }
                catch (Exception ex) { Log.Debug("VMHOST", "опроса статуса: " + ex.Message); }

                await Task.Delay(300).ConfigureAwait(false);
            }

            try { process.Kill(true); } catch { }
            throw new Hv86Exception("ВМ не вышла в состояние running за 30 секунд");
        }

        /// <summary>Мягкое завершение: vmhost сохраняет диск и выходит.</summary>
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