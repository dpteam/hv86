using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hv86.Core;
using Hv86.Vm;

namespace Hv86.App
{
    /// <summary>
    /// `hv86.exe --htest`: сквозной тест headless-движка.
    ///
    /// Поднимает движок в процессе, создаёт ВМ с engine=headless, ждёт
    /// настоящую загрузку FreeDOS (текст на VGA-экране приходит из Node),
    /// снимает снапшот, останавливает и удаляет.
    ///
    /// Требует Node 20+ и `npm install` в engine\ (tools\fetch-engine.ps1).
    /// Если их нет — выход с кодом 3 и понятным текстом, а не молчаливый провал.
    /// </summary>
    public static class HeadlessTest
    {
        private static int _failures;

        public static int Run()
        {
            string node = NodeRuntime.Executable;

            if (string.IsNullOrEmpty(node))
            {
                Console.Error.WriteLine("SKIP: " + NodeRuntime.Error);
                return 3;
            }

            string engineDir = Paths.EngineDir;

            if (!File.Exists(Path.Combine(engineDir, "vmhost.mjs")))
            {
                Console.Error.WriteLine("SKIP: engine\\vmhost.mjs not found");
                return 3;
            }

            if (!Directory.Exists(Path.Combine(engineDir, "node_modules", "v86")))
            {
                Console.Error.WriteLine("SKIP: engine\\node_modules\\v86 not installed (run tools\\fetch-engine.ps1)");
                return 3;
            }

            string root = Path.Combine(Path.GetTempPath(), "hv86-htest-" + DateTime.Now.ToString("HHmmss"));

            Environment.SetEnvironmentVariable("HV86_ROOT", root);
            Paths.ForceRoot(root);
            Paths.EnsureDirectories();

            Console.WriteLine("hv86 headless test");
            Console.WriteLine("  node:   " + node);
            Console.WriteLine("  engine: " + engineDir);
            Console.WriteLine("  root:   " + root);

            // ---- 9p-сервер: собственный протокольный тест (без гостя)
            string ninePTest = Path.Combine(engineDir, "test-9p.mjs");

            if (File.Exists(ninePTest))
            {
                try
                {
                    ProcessStartInfo psi = new ProcessStartInfo
                    {
                        FileName = node,
                        Arguments = "\"" + ninePTest + "\"",
                        WorkingDirectory = engineDir,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using (Process process = Process.Start(psi))
                    {
                        string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                        process.WaitForExit(120000);

                        if (process.ExitCode == 0) Ok("9p protocol", "9P2000.L server passed its own test");
                        else
                        {
                            Fail("9p protocol test failed");
                            Console.WriteLine(output);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Fail("9p protocol test: " + ex.Message);
                }
            }

            string shareDir = Path.Combine(root, "share");
            Directory.CreateDirectory(shareDir);
            File.WriteAllText(Path.Combine(shareDir, "hello.txt"), "hv86 9p");

            ServiceRunner runner = new ServiceRunner();
            PipeClient client = new PipeClient(PipeNames.Service);

            try
            {
                runner.Start();

                if (!client.ConnectAsync(8000).GetAwaiter().GetResult())
                {
                    Fail("cannot connect to the engine pipe");
                    return 1;
                }

                Dictionary<string, object> created = Call(client, "vm.create", Json.Obj(
                    "name", "htest-freedos",
                    "preset", "freedos",
                    "memory_mb", 32,
                    "engine", "headless",
                    "share_path", shareDir,
                    "share_readonly", true));

                string id = Json.GetString(created, "id", "");
                Ok("vm.create (headless)", id);

                if (Json.GetString(created, "share_path", "") == shareDir) Ok("share", "9p folder attached");
                else Fail("share path did not stick: " + Json.GetString(created, "share_path", ""));

                Dictionary<string, object> view = Call(client, "vm.get", Json.Obj("id", id));
                if (Json.GetString(view, "engine", "") == "headless") Ok("engine", "headless");
                else Fail("engine is not headless: " + Json.GetString(view, "engine", ""));

                Call(client, "vm.start", Json.Obj("id", id), 180000);
                WaitState(client, id, "running", 120000);
                Ok("vm.start", "running");

                string screen = WaitScreen(id, 120000);

                if (screen.IndexOf("FreeDOS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    screen.IndexOf("A:\\>", StringComparison.Ordinal) >= 0 ||
                    screen.IndexOf("SeaBIOS", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    Ok("vga text", FirstMeaningfulLine(screen));
                }
                else
                {
                    Fail("no boot text on the VGA screen");
                    Console.WriteLine("----- screen -----");
                    Console.WriteLine(screen);
                    Console.WriteLine("------------------");
                }

                Dictionary<string, object> snapshot = Call(client, "vm.snapshot",
                    Json.Obj("id", id, "name", "headless-first"), 300000);

                Ok("vm.snapshot", Math.Round(Json.GetLong(snapshot, "size", 0) / 1024.0 / 1024.0, 1) + " MB");

                // окно консоли headless: проверяем, что оно открывается, подключается
                // к движку и переживает отрисовку (глазами это не проверить)
                try
                {
                    using (HeadlessConsoleWindow window = new HeadlessConsoleWindow(id, "htest"))
                    {
                        window.StartPosition = System.Windows.Forms.FormStartPosition.Manual;
                        window.Location = new System.Drawing.Point(-32000, -32000);
                        window.Show();

                        DateTime until = DateTime.UtcNow.AddSeconds(5);
                        while (DateTime.UtcNow < until)
                        {
                            System.Windows.Forms.Application.DoEvents();
                            Thread.Sleep(50);
                        }

                        window.Close();
                    }

                    Ok("console window", "opened, painted and closed");
                }
                catch (Exception ex)
                {
                    Fail("console window: " + ex.GetType().Name + ": " + ex.Message);
                }

                object snapshots = CallRaw(client, "vm.snapshots", Json.Obj("id", id));
                List<object> list = snapshots as List<object>;

                if (list == null || list.Count == 0) Fail("snapshot list is empty");
                else Ok("vm.snapshots", list.Count + " snapshot(s)");

                Call(client, "vm.stop", Json.Obj("id", id), 180000);
                WaitState(client, id, "stopped", 60000);
                Ok("vm.stop", "stopped");

                Call(client, "vm.delete", Json.Obj("id", id), 120000);

                object remaining = CallRaw(client, "vm.list", null);
                List<object> vms = remaining as List<object>;

                Ok("vm.delete", (vms == null ? 0 : vms.Count) + " vm(s) left");

                if (vms != null && vms.Count > 0) Fail("VM was not removed");
            }
            catch (Exception ex)
            {
                Fail(ex.GetType().Name + ": " + ex.Message);
                Log.Exception("HTEST", "исключение", ex);
            }
            finally
            {
                try { client.Dispose(); } catch { }
                try { runner.Stop(); } catch { }

                try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
            }

            Console.WriteLine(_failures == 0 ? "HTEST: OK" : "HTEST: FAILED (" + _failures + ")");
            return _failures == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------ помощники

        private static string WaitScreen(string vmId, int timeoutMs)
        {
            string latest = "";

            using (PipeClient vm = new PipeClient(PipeNames.VmHost(vmId)))
            {
                if (!vm.ConnectAsync(8000).GetAwaiter().GetResult())
                    throw new Hv86Exception("cannot connect to the vm pipe");

                vm.EventReceived += delegate (string name, Dictionary<string, object> data)
                {
                    if (name != "vm.screen") return;

                    List<object> lines = Json.GetList(data, "lines");
                    if (lines == null) return;

                    StringBuilder text = new StringBuilder();
                    foreach (object line in lines) text.AppendLine(line as string ?? "");
                    latest = text.ToString();
                };

                DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

                while (DateTime.UtcNow < deadline)
                {
                    if (latest.IndexOf("FreeDOS", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        latest.IndexOf("A:\\>", StringComparison.Ordinal) >= 0)
                        return latest;

                    Thread.Sleep(300);
                }
            }

            return latest;
        }

        private static string FirstMeaningfulLine(string screen)
        {
            if (string.IsNullOrEmpty(screen)) return "";

            foreach (string line in screen.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length == 0) continue;

                // ANSI-последовательности в лог не тащим
                StringBuilder clean = new StringBuilder();
                for (int i = 0; i < trimmed.Length; i++)
                {
                    if (trimmed[i] == '\u001b')
                    {
                        int end = trimmed.IndexOf('m', i);
                        if (end < 0) break;
                        i = end;
                        continue;
                    }

                    clean.Append(trimmed[i]);
                }

                string result = clean.ToString().Trim();
                if (result.Length > 0) return result.Length > 70 ? result.Substring(0, 70) : result;
            }

            return "";
        }

        private static object CallRaw(PipeClient client, string method, Dictionary<string, object> parameters,
            int timeoutMs = 60000)
        {
            return client.CallForResultAsync(method, parameters, timeoutMs).GetAwaiter().GetResult();
        }

        private static Dictionary<string, object> Call(PipeClient client, string method,
            Dictionary<string, object> parameters, int timeoutMs = 60000)
        {
            return CallRaw(client, method, parameters, timeoutMs) as Dictionary<string, object>;
        }

        private static void WaitState(PipeClient client, string id, string expected, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            Dictionary<string, object> last = null;

            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                last = Call(client, "vm.get", Json.Obj("id", id), 15000);
                string state = Json.GetString(last, "state", "");

                if (state == expected) return;

                if (state == "failed")
                    throw new Hv86Exception("VM failed: " + Json.GetString(last, "last_error", ""));

                Thread.Sleep(300);
            }

            throw new Hv86Exception("VM did not reach '" + expected + "' (last: " +
                                    Json.GetString(last, "state", "?") + ")");
        }

        private static void Ok(string what, string detail)
        {
            Console.WriteLine("  [ok]   " + what + (string.IsNullOrEmpty(detail) ? "" : " - " + detail));
        }

        private static void Fail(string text)
        {
            _failures++;
            Console.WriteLine("  [FAIL] " + text);
        }
    }
}