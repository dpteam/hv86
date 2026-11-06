using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using Hv86.App;
using Hv86.Core;
using Hv86.VmHost;

namespace Hv86
{
    /// <summary>
    /// Точка входа. Один exe - четыре роли:
    ///
    ///   (без аргументов)      интерфейс: Hyper-V-подобный менеджер + WebView2
    ///   --service [действие]  тело службы Windows (install/start/stop/...)
    ///   --vmhost &lt;id&gt;         процесс одной ВМ (v86 в WebView2)
    ///   --smoke &lt;образ&gt;       самотест: загрузка, снапшот, восстановление
    ///
    /// Порядок веток важен: режим службы обязан вызвать
    /// StartServiceCtrlDispatcher в первые 30 секунд и не имеет права
    /// создавать окна (session 0), поэтому он обрабатывается раньше любых
    /// WinForms-настроек.
    /// </summary>
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            bool hasFlag = HasFlag(args, "--service");
            bool isVmHost = HasFlag(args, "--vmhost");
            bool isUi = !hasFlag && !isVmHost && !HasFlag(args, "--smoke") &&
                        !HasFlag(args, "--selftest") && !HasFlag(args, "--version") &&
                        !HasFlag(args, "--itest") && !HasFlag(args, "--engine") &&
                        !HasFlag(args, "--unit") && !HasFlag(args, "--cli") && !HasFlag(args, "--ctl") &&
                        !HasFlag(args, "--htest");

            if (!isUi) ConsoleHelper.Attach();

            Paths.EnsureDirectories();

            // ---------------------------------------------------------- служба
            if (hasFlag)
            {
                Log.Role = "svc";
                Log.MinLevel = LogLevel.Info;
                Log.Info("SVC", AppInfo.Summary() + "; " + SessionLauncher.Describe());

                bool autoStart = !HasFlag(args, "--demand");
                return ServiceManager.RunCli(NextTo(args, "--service", "status"), autoStart);
            }

            // ------------------------------------------------------------ vmhost
            if (isVmHost)
            {
                string id = NextTo(args, "--vmhost", "");
                if (string.IsNullOrEmpty(id))
                {
                    Console.WriteLine("--vmhost requires a VM id");
                    return 2;
                }

                Log.Role = "vm-" + id;
                Log.MinLevel = LogLevel.Info;
                Log.Info("VMHOST", AppInfo.Summary() + " для " + id);

                bool showConsole = HasFlag(args, "--console");
                string restore = NextTo(args, "--restore", null);

                return new VmHostRunner(id, showConsole, restore, false).Run();
            }

            // ------------------------------------------------------------ smoke
            if (HasFlag(args, "--smoke"))
            {
                Log.Role = "smoke";
                Log.MinLevel = LogLevel.Debug;

                string image = NextTo(args, "--smoke", "");
                return SmokeTest.Run(image);
            }

            // --------------------------------------------------------- selftest
            if (HasFlag(args, "--selftest"))
            {
                Log.Role = "selftest";
                return SelfTest.Run();
            }

            // ------------------------------------------------------------- unit tests
            if (HasFlag(args, "--unit"))
            {
                Log.Role = "unit";
                Log.MinLevel = LogLevel.Warn;
                return UnitTest.Run();
            }

            // ------------------------------------------------------------ консоль
            if (HasFlag(args, "--cli") || HasFlag(args, "--ctl"))
            {
                Log.Role = "cli";
                Log.MinLevel = LogLevel.Warn;

                List<string> rest = new List<string>();

                foreach (string arg in args)
                {
                    if (string.Equals(arg, "--cli", StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(arg, "--ctl", StringComparison.OrdinalIgnoreCase)) continue;

                    rest.Add(arg);
                }

                return Cli.Run(rest.ToArray());
            }

            // --------------------------------------------------- integration test
            if (HasFlag(args, "--itest"))
            {
                Log.Role = "itest";
                Log.MinLevel = LogLevel.Debug;
                return IntegrationTest.Run(NextTo(args, "--itest", ""));
            }

            // ------------------------------------------- headless integration test
            if (HasFlag(args, "--htest"))
            {
                Log.Role = "htest";
                Log.MinLevel = LogLevel.Info;
                return HeadlessTest.Run();
            }

            // ----------------------------------------------- движок без службы
            if (HasFlag(args, "--engine"))
            {
                Log.Role = "engine";
                Log.MinLevel = LogLevel.Info;
                Log.Info("ENGINE", AppInfo.Summary() + "; " + SessionLauncher.Describe());

                return RunEngine();
            }

            if (HasFlag(args, "--version"))
            {
                Console.WriteLine(AppInfo.Summary());
                return 0;
            }

            // --------------------------------------------------------------- UI
            Log.Role = "ui";
            Log.MinLevel = LogLevel.Info;
            Log.Info("UI", AppInfo.Summary() + "; " + Elevation.CurrentUser);

            try { Application.SetHighDpiMode(HighDpiMode.PerMonitorV2); }
            catch (Exception ex) { Log.Warn("UI", "DPI: " + ex.Message); }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (!Elevation.IsElevated)
            {
                // манифест требует администратора; сюда попадаем только при
                // запуске в обход манифеста
                Log.Warn("UI", "запуск без прав администратора: служба и ВМ будут недоступны");
            }

            using (SingleInstance single = new SingleInstance("Local\\hv86-ui"))
            {
                if (!single.TryAcquire(0))
                {
                    Log.Info("UI", "интерфейс уже запущен");
                    return 0;
                }

                using (MainForm form = new MainForm())
                    Application.Run(form);
            }

            Log.Info("UI", "выход");
            return 0;
        }

        /// <summary>
        /// Движок без установки службы: удобно для отладки и для портативного
        /// режима. Интерфейс подключается к нему по тому же каналу, что и к
        /// службе, - разницы для UI нет.
        /// </summary>
        private static int RunEngine()
        {
            ServiceRunner runner = new ServiceRunner();
            bool stopping = false;

            Console.CancelKeyPress += delegate (object sender, ConsoleCancelEventArgs e)
            {
                e.Cancel = true;
                stopping = true;
            };

            runner.Start();

            Console.WriteLine("hv86 engine is running. Press Enter to stop.");

            try
            {
                while (!stopping)
                {
                    if (Console.KeyAvailable)
                    {
                        Console.ReadKey(true);
                        break;
                    }

                    System.Threading.Thread.Sleep(200);
                }
            }
            catch
            {
                // нет консоли - просто работаем до внешней остановки
                System.Threading.Thread.Sleep(System.Threading.Timeout.Infinite);
            }

            runner.Stop();
            return 0;
        }

        private static bool HasFlag(string[] args, string flag)
        {
            if (args == null) return false;
            foreach (string a in args)
                if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        /// <summary>Значение флага: `--vmhost abc` -> "abc".</summary>
        private static string NextTo(string[] args, string flag, string fallback)
        {
            if (args == null) return fallback;

            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) continue;
                if (i + 1 >= args.Length) return fallback;

                string value = args[i + 1];
                if (value.StartsWith("--", StringComparison.Ordinal)) return fallback;
                return value;
            }

            return fallback;
        }
    }

    /// <summary>
    /// Вывод в консоль из WinExe-приложения. Без этого `hv86.exe --service
    /// install` из cmd не печатает ничего: у GUI-подсистемы нет консоли.
    /// </summary>
    internal static class ConsoleHelper
    {
        private const int AttachParentProcess = -1;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AttachConsole(int processId);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AllocConsole();

        public static void Attach()
        {
            try
            {
                if (!AttachConsole(AttachParentProcess)) AllocConsole();

                Stream stdout = Console.OpenStandardOutput();
                StreamWriter writer = new StreamWriter(stdout, new UTF8Encoding(false)) { AutoFlush = true };
                Console.SetOut(writer);

                Stream stderr = Console.OpenStandardError();
                Console.SetError(new StreamWriter(stderr, new UTF8Encoding(false)) { AutoFlush = true });
            }
            catch
            {
                // не смогли - не повод падать
            }
        }
    }
}