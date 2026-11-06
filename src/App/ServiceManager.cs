using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Hv86.Core;

namespace Hv86.App
{
    /// <summary>Состояние службы для интерфейса.</summary>
    public sealed class ServiceStatus
    {
        public bool Installed;
        public bool Running;
        public bool StartPending;
        public bool StopPending;
        public string State = "not installed";
        public string Problem = "";
        public string Path = "";

        public string Text()
        {
            if (!Installed) return State;
            if (Problem.Length > 0) return State + " (" + Problem + ")";
            return State;
        }

        public static ServiceStatus Unknown()
        {
            return new ServiceStatus { Installed = false, State = "unknown" };
        }
    }

    /// <summary>Результат операции над службой.</summary>
    public sealed class ServiceResult
    {
        public bool Ok;
        public string Message = "";

        public static ServiceResult Good(string message) { return new ServiceResult { Ok = true, Message = message }; }
        public static ServiceResult Bad(string message) { return new ServiceResult { Ok = false, Message = message }; }
    }

    /// <summary>
    /// Установка/удаление/управление службой через sc.exe.
    ///
    /// Почему sc.exe, а не P/Invoke: установка - это одна команда, и повторять
    /// CreateService/ChangeServiceConfig в управляемом коде значит заново
    /// отлаживать уже отлаженную в Windows логику. P/Invoke остался только в
    /// теле службы, где без него нельзя.
    ///
    /// `chcp 437` перед командами: без него на русской Windows sc отвечает в
    /// OEM-кодировке, и разобрать RUNNING/STOPPED невозможно.
    /// </summary>
    public static class ServiceManager
    {
        public static string ExePath
        {
            get
            {
                try
                {
                    string path = Process.GetCurrentProcess().MainModule.FileName;
                    if (!string.IsNullOrEmpty(path)) return path;
                }
                catch { }

                return Paths.ExePath;
            }
        }

        public static ServiceStatus Query()
        {
            ServiceStatus status = new ServiceStatus();

            string output = Cmd("sc query \"" + AppInfo.ServiceName + "\"");

            if (output.IndexOf("FAILED 1060", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0 ||
                output.IndexOf("не найдена", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                status.Installed = false;
                status.State = "not installed";
                return status;
            }

            status.Installed = true;

            if (output.IndexOf("RUNNING", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                status.Running = true;
                status.State = "running";
            }
            else if (output.IndexOf("START_PENDING", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                status.StartPending = true;
                status.State = "starting";
            }
            else if (output.IndexOf("STOP_PENDING", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                status.StopPending = true;
                status.State = "stopping";
            }
            else if (output.IndexOf("STOPPED", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                status.State = "stopped";
            }
            else
            {
                status.State = FirstLine(output);
            }

            string config = Cmd("sc qc \"" + AppInfo.ServiceName + "\"");
            foreach (string line in config.Split('\n'))
            {
                string trimmed = line.Trim();

                if (trimmed.StartsWith("BINARY_PATH_NAME", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = trimmed.IndexOf(':');
                    if (colon > 0) status.Path = trimmed.Substring(colon + 1).Trim();
                    break;
                }
            }

            if (status.Path.Length > 0 && status.Path.IndexOf(ExePath, StringComparison.OrdinalIgnoreCase) < 0)
                status.Problem = "installed from another location: " + status.Path;

            return status;
        }

        public static ServiceResult Install(bool autoStart)
        {
            if (!Elevation.IsElevated)
                return ServiceResult.Bad("administrator rights required");

            if (Query().Installed)
                return ServiceResult.Bad("service is already installed: uninstall it first");

            string exe = ExePath;
            if (!File.Exists(exe)) return ServiceResult.Bad("cannot find own exe: " + exe);

            // под `dotnet hv86.dll` exe — это dotnet.exe: служба с такой
            // binPath запустит не то, что нужно
            if (exe.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                return ServiceResult.Bad("run hv86.exe directly (not `dotnet hv86.dll`) to install the service");

            // binPath в кавычках: путь с пробелами иначе развалится на аргументы
            string binPath = "\"\\\"" + exe + "\\\" --service\"";

            string output = Cmd("sc create \"" + AppInfo.ServiceName + "\" binPath= " + binPath +
                                " start= " + (autoStart ? "auto" : "demand") +
                                " obj= LocalSystem DisplayName= \"" + AppInfo.ServiceDisplayName + "\"");

            if (!Created(output))
            {
                Log.Warn("SVC", "sc create: " + output.Trim());
                return ServiceResult.Bad("cannot create service: " + FirstLine(output));
            }

            Cmd("sc description \"" + AppInfo.ServiceName +
                "\" \"hv86 hypervisor engine: runs v86 virtual machines, snapshots and fault tolerance.\"");

            // восстановление при падении: «установил и забыл» не должно превращаться
            // в «однажды перестало»
            Cmd("sc failure \"" + AppInfo.ServiceName +
                "\" reset= 86400 actions= restart/5000/restart/10000/restart/30000");

            Log.Info("SVC", "служба установлена: " + exe + ", автозапуск=" + autoStart);
            return ServiceResult.Good("service installed" + (autoStart ? ", starts with Windows" : ""));
        }

        public static ServiceResult Uninstall()
        {
            if (!Elevation.IsElevated)
                return ServiceResult.Bad("administrator rights required");

            ServiceStatus status = Query();
            if (!status.Installed) return ServiceResult.Bad("service is not installed");

            if (status.Running || status.StartPending)
            {
                Stop();
                WaitForStopped(20000);
            }

            string output = Cmd("sc delete \"" + AppInfo.ServiceName + "\"");

            if (output.IndexOf("SUCCESS", StringComparison.OrdinalIgnoreCase) < 0 &&
                output.IndexOf("УСПЕХ", StringComparison.OrdinalIgnoreCase) < 0 &&
                Query().Installed)
            {
                Log.Warn("SVC", "sc delete: " + output.Trim());
                return ServiceResult.Bad("cannot delete service: " + FirstLine(output));
            }

            Log.Info("SVC", "служба удалена");
            return ServiceResult.Good("service removed");
        }

        public static ServiceResult Start()
        {
            if (!Elevation.IsElevated)
                return ServiceResult.Bad("administrator rights required");

            ServiceStatus status = Query();
            if (!status.Installed) return ServiceResult.Bad("service is not installed");
            if (status.Running) return ServiceResult.Good("service is already running");

            string output = Cmd("sc start \"" + AppInfo.ServiceName + "\"");

            if (output.IndexOf("1053", StringComparison.Ordinal) >= 0)
            {
                Log.Error("SVC", "служба не ответила SCM: " + output.Trim());
                return ServiceResult.Bad("service did not answer SCM (1053): reinstall it");
            }

            for (int i = 0; i < 40; i++)
            {
                System.Threading.Thread.Sleep(250);

                ServiceStatus after = Query();
                if (after.Running) return ServiceResult.Good("service started");
            }

            ServiceStatus final = Query();
            if (final.Running) return ServiceResult.Good("service started");

            Log.Warn("SVC", "sc start: " + output.Trim());
            return ServiceResult.Bad("cannot start: " + final.State);
        }

        public static ServiceResult Stop()
        {
            if (!Elevation.IsElevated)
                return ServiceResult.Bad("administrator rights required");

            ServiceStatus status = Query();
            if (!status.Installed) return ServiceResult.Bad("service is not installed");
            if (!status.Running && !status.StartPending) return ServiceResult.Good("service is already stopped");

            Cmd("sc stop \"" + AppInfo.ServiceName + "\"");

            if (!WaitForStopped(30000))
            {
                Log.Warn("SVC", "служба не остановилась за 30 с");
                return ServiceResult.Bad("service did not stop within 30 seconds");
            }

            return ServiceResult.Good("service stopped");
        }

        private static bool WaitForStopped(int timeoutMs)
        {
            int waited = 0;

            while (waited < timeoutMs)
            {
                System.Threading.Thread.Sleep(500);
                waited += 500;

                ServiceStatus status = Query();
                if (!status.Running && !status.StopPending && !status.StartPending) return true;
            }

            return false;
        }

        private static bool Created(string output)
        {
            if (output.IndexOf("SUCCESS", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (output.IndexOf("УСПЕХ", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return Query().Installed;
        }

        private static string Cmd(string arguments)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c chcp 437 >nul & " + arguments,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                using (Process process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    string error = process.StandardError.ReadToEnd();

                    process.WaitForExit(20000);
                    return output + (error.Length > 0 ? "\n" + error : "");
                }
            }
            catch (Exception ex)
            {
                Log.Warn("SVC", "команда не выполнилась: " + ex.Message);
                return "error: " + ex.Message;
            }
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0) return trimmed.Length > 160 ? trimmed.Substring(0, 160) : trimmed;
            }

            return "";
        }

        /// <summary>CLI: install|uninstall|start|stop|status|run.</summary>
        public static int RunCli(string action, bool autoStart)
        {
            switch ((action ?? "").ToLowerInvariant())
            {
                case "install":
                {
                    ServiceResult result = Install(autoStart);
                    Console.WriteLine(result.Message);
                    return result.Ok ? 0 : 1;
                }

                case "uninstall":
                case "delete":
                case "remove":
                {
                    ServiceResult result = Uninstall();
                    Console.WriteLine(result.Message);
                    return result.Ok ? 0 : 1;
                }

                case "start":
                {
                    ServiceResult result = Start();
                    Console.WriteLine(result.Message);
                    return result.Ok ? 0 : 1;
                }

                case "stop":
                {
                    ServiceResult result = Stop();
                    Console.WriteLine(result.Message);
                    return result.Ok ? 0 : 1;
                }

                case "run":
                    return ServiceHost.Run(AppInfo.ServiceName);
            }

            ServiceStatus status = Query();

            Console.WriteLine("== service " + AppInfo.ServiceName + " ==");
            Console.WriteLine("  state: " + status.Text());
            if (status.Path.Length > 0) Console.WriteLine("  exe:   " + status.Path);
            if (status.Problem.Length > 0) Console.WriteLine("  note:  " + status.Problem);
            Console.WriteLine("  ours:  " + ExePath);

            return 0;
        }
    }
}