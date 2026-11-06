using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Hv86.Core;
using Hv86.Vm;

namespace Hv86.App
{
    /// <summary>
    /// `hv86.exe --itest`: сквозной тест службы без установки службы.
    ///
    /// Поднимает ServiceRunner прямо в этом процессе, подключается к нему
    /// по тому же named pipe, что и интерфейс, и проходит полный сценарий:
    /// создать ВМ → загрузить → снапшот → убить vmhost → дождаться
    /// автоподъёма → откат к снапшоту → остановить → удалить.
    ///
    /// Зачем: это единственный способ проверить связку «служба ↔ vmhost ↔
    /// v86 ↔ снапшоты ↔ watchdog» без ручных кликов и без прав администратора.
    /// </summary>
    public static class IntegrationTest
    {
        private static int _failures;

        public static int Run(string keepFlag)
        {
            bool keep = string.Equals(keepFlag, "keep", StringComparison.OrdinalIgnoreCase);

            string root = Path.Combine(Path.GetTempPath(), "hv86-itest-" + DateTime.Now.ToString("HHmmss"));

            Environment.SetEnvironmentVariable("HV86_ROOT", root);
            Paths.ForceRoot(root);
            Paths.EnsureDirectories();

            Console.WriteLine("hv86 integration test");
            Console.WriteLine("  root: " + root);

            ServiceRunner runner = new ServiceRunner();
            PipeClient client = new PipeClient(PipeNames.Service);
            List<string> events = new List<string>();

            try
            {
                runner.Start();

                if (!client.ConnectAsync(8000).GetAwaiter().GetResult())
                {
                    Fail("cannot connect to the engine pipe");
                    return 1;
                }

                client.EventReceived += delegate (string name, Dictionary<string, object> data)
                {
                    lock (events)
                    {
                        if (name == "ft.event")
                            events.Add(Json.GetString(data, "kind", "") + ": " + Json.GetString(data, "text", ""));
                        else if (name == "vm.forward")
                            events.Add("forward: " + Json.GetString(data, "text", ""));
                    }

                    if (name == "ft.event" || name == "vm.state")
                        Console.WriteLine("  event " + name + ": " + Json.Stringify(data));
                };

                string shareDir = Path.Combine(root, "share");
                Directory.CreateDirectory(shareDir);
                File.WriteAllText(Path.Combine(shareDir, "hello.txt"), "hv86");

                Dictionary<string, object> created = Call(client, "vm.create", Json.Obj(
                    "name", "itest-freedos",
                    "preset", "freedos",
                    "memory_mb", 32,
                    "disk_mb", 0,
                    "share_path", shareDir,
                    "share_readonly", true));

                string id = Json.GetString(created, "id", "");
                Ok("vm.create", id);

                // проброс портов: host:2399 -> guest:22 (в FreeDOS TCP нет,
                // поэтому ждём именно корректный отказ, а не данные)
                const int forwardPort = 2399;

                Call(client, "vm.update", Json.Obj(
                    "id", id,
                    "forwards", new List<object>
                    {
                        Json.Obj("host_port", forwardPort, "guest_port", 22, "bind", "127.0.0.1")
                    }), 30000);

                Call(client, "vm.start", Json.Obj("id", id));
                Dictionary<string, object> running = WaitState(client, id, "running", 90000);

                // даём гостю реально загрузиться: снапшот «на нуле инструкций»
                // технически валиден, но проверяет гораздо меньше
                WaitInstructions(client, id, 1000000, 60000);
                Ok("guest executing", "instructions=" + Json.GetLong(Call(client, "vm.get", Json.Obj("id", id)),
                    "instructions", 0));

                // web-движок: общая папка через синхронный XHR к /share.
                // Проверяем transport и настоящий stat файла из шары.
                // Команда живёт в vmhost, поэтому идём прямо в его канал.
                Dictionary<string, object> xhr = null;

                using (PipeClient vmPipe = new PipeClient(PipeNames.VmHost(id)))
                {
                    if (!vmPipe.ConnectAsync(5000).GetAwaiter().GetResult())
                        Fail("cannot connect to the vm pipe for the xhr probe");
                    else
                        xhr = vmPipe.CallForResultAsync("vm.debug.xhr", null, 30000).GetAwaiter().GetResult()
                            as Dictionary<string, object>;
                }

                string xhrBody = Json.GetString(xhr, "body", "");

                if (xhr != null && Json.GetBool(xhr, "ok", false) &&
                    xhrBody.IndexOf("\"size\"", StringComparison.Ordinal) >= 0)
                    Ok("web 9p transport", "sync XHR /share works: " + xhrBody);
                else
                    Fail("sync XHR /share failed: " + Json.Stringify(xhr));

                if (xhr != null && Json.GetBool(xhr, "filesystem", false))
                    Ok("web 9p handler", "fs9p/fs-xhr modules loaded in the page");
                else
                    Fail("9p modules did not load in the console page");

                string forwardResult = ProbeForward("127.0.0.1", forwardPort, 45000);
                Ok("port forward", forwardResult);

                if (forwardResult.IndexOf("closed", StringComparison.OrdinalIgnoreCase) < 0 &&
                    forwardResult.IndexOf("reset", StringComparison.OrdinalIgnoreCase) < 0)
                    Fail("port forward did not behave: " + forwardResult);

                bool forwardEvent = false;
                lock (events)
                {
                    for (int i = 0; i < events.Count; i++)
                        if (events[i].StartsWith("forward:", StringComparison.Ordinal)) forwardEvent = true;
                }

                if (!forwardEvent) Fail("no vm.forward event was reported");
                else Ok("forward event", "vmhost reported the forwarding activity");

                Call(client, "vm.snapshot", Json.Obj("id", id, "name", "first"), 300000);
                object snapshots = CallRaw(client, "vm.snapshots", Json.Obj("id", id));
                List<object> list = snapshots as List<object>;
                Ok("vm.snapshot", (list == null ? 0 : list.Count) + " snapshot(s)");

                if (list == null || list.Count == 0)
                {
                    Fail("snapshot list is empty");
                    return 1;
                }

                string snapshotId = Json.GetString(list[0] as Dictionary<string, object>, "id", "");

                // ---- падение vmhost: watchdog должен поднять ВМ сам
                // сначала ждём, пока появится чекпоинт кольца: именно из него
                // ВМ должна восстановиться, а не холодной загрузкой
                Console.WriteLine("  waiting for a ring checkpoint ...");
                WaitCheckpoints(client, id, 1, 60000);
                Ok("ring checkpoint", "count=" + Json.GetInt(Call(client, "vm.get", Json.Obj("id", id)), "checkpoints", 0));

                Call(client, "vm.debug.kill", Json.Obj("id", id));
                Console.WriteLine("  killed vmhost, waiting for the watchdog ...");

                WaitRestarts(client, id, 1, 90000);

                Dictionary<string, object> recovered = WaitState(client, id, "running", 120000);
                int restarts = Json.GetInt(recovered, "restarts", 0);

                bool fromCheckpoint = false;
                lock (events)
                {
                    for (int i = 0; i < events.Count; i++)
                        if (events[i].IndexOf("Восстановление из чекпоинта", StringComparison.Ordinal) >= 0)
                            fromCheckpoint = true;
                }

                if (!fromCheckpoint) Fail("restart did not report a checkpoint restore");
                else Ok("checkpoint restore", "VM came back from the ring checkpoint");

                if (restarts >= 1) Ok("watchdog restart", "restarts=" + restarts);
                else Fail("restarts counter did not grow: " + restarts);

                // ---- откат к снапшоту
                Call(client, "vm.revert", Json.Obj("id", id, "snapshot", snapshotId), 300000);
                Dictionary<string, object> reverted = WaitState(client, id, "running", 90000);
                Ok("vm.revert", "state=" + Json.GetString(reverted, "state", ""));

                // ---- сеть: relay (WISP) и произвольный TCP из гостя
                // поднимаем локальный echo-сервер и просим relay соединиться с ним
                using (System.Net.Sockets.TcpListener echo = new System.Net.Sockets.TcpListener(
                           System.Net.IPAddress.Loopback, 0))
                {
                    echo.Start();

                    int echoPort = ((System.Net.IPEndPoint)echo.LocalEndpoint).Port;

                    System.Threading.Tasks.Task echoTask = System.Threading.Tasks.Task.Run(async delegate
                    {
                        try
                        {
                            using (System.Net.Sockets.TcpClient peer = await echo.AcceptTcpClientAsync())
                            using (System.Net.Sockets.NetworkStream stream = peer.GetStream())
                            {
                                byte[] buffer = new byte[256];
                                int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                                if (read > 0) await stream.WriteAsync(buffer, 0, read);
                            }
                        }
                        catch
                        {
                            // тест сам решит, что делать
                        }
                    });

                    Call(client, "vm.update", Json.Obj("id", id, "net_mode", "relay"), 30000);
                    Call(client, "vm.stop", Json.Obj("id", id), 120000);
                    Call(client, "vm.start", Json.Obj("id", id));
                    WaitState(client, id, "running", 90000);

                    int relayPort = WaitRelayPort(client, id, 60000);
                    Ok("wisp relay listening", "127.0.0.1:" + relayPort);

                    // самое важное: страница v86 сама подключилась к релею
                    // (если бы ws:// блокировался из-за схемы страницы, здесь было бы 0)
                    WaitRelayClients(client, id, 1, 45000);
                    Ok("v86 joined the relay", "websocket from the console page is alive");

                    string relayDetail;
                    bool relayOk = WispTestClient.Echo(relayPort, echoPort, "hv86-relay", out relayDetail);

                    if (relayOk) Ok("wisp relay TCP", relayDetail);
                    else Fail("wisp relay: " + relayDetail);

                    // ---- правила исходящего трафика: safe обязан отрезать loopback
                    Call(client, "vm.update", Json.Obj("id", id, "egress_mode", "safe"), 30000);
                    System.Threading.Thread.Sleep(400);

                    string blockedDetail;
                    bool blockedOk = WispTestClient.Echo(relayPort, echoPort, "hv86-relay", out blockedDetail);

                    if (!blockedOk && blockedDetail.IndexOf("0x48", StringComparison.OrdinalIgnoreCase) >= 0)
                        Ok("egress policy", "safe mode blocked loopback (" + blockedDetail + ")");
                    else
                        Fail("egress policy did not block loopback: " + blockedDetail);

                    try { echo.Stop(); } catch { }
                }

                // ---- настройки: имя и параметры FT
                Dictionary<string, object> updated = Call(client, "vm.update", Json.Obj(
                    "id", id,
                    "name", "itest-renamed",
                    "ft_interval_ms", 20000,
                    "ft_keep", 2,
                    "restart", "always",
                    "autostart", false), 30000);

                if (Json.GetString(updated, "name", "") == "itest-renamed" &&
                    Json.GetInt(updated, "interval_ms", 0) == 20000 &&
                    Json.GetInt(updated, "ft_keep", 0) == 2)
                    Ok("vm.update", "name/interval/keep applied");
                else
                    Fail("vm.update did not apply: " + Json.Stringify(updated));

                // ---- остановка, клон, экспорт, импорт
                Call(client, "vm.stop", Json.Obj("id", id), 120000);
                WaitState(client, id, "stopped", 60000);

                Dictionary<string, object> clone = Call(client, "vm.clone",
                    Json.Obj("id", id, "name", "itest-clone"), 300000);
                string cloneId = Json.GetString(clone, "id", "");
                Ok("vm.clone", cloneId);

                string archive = Path.Combine(Path.GetTempPath(), "hv86-itest-" + cloneId + VmArchive.Extension);

                Dictionary<string, object> exported = Call(client, "vm.export",
                    Json.Obj("id", cloneId, "path", archive), 600000);

                long archiveSize = Json.GetLong(exported, "size", 0);

                if (File.Exists(archive) && archiveSize > 0) Ok("vm.export", archiveSize + " bytes");
                else Fail("vm.export produced no file");

                Dictionary<string, object> imported = Call(client, "vm.import",
                    Json.Obj("path", archive), 600000);
                string importedId = Json.GetString(imported, "id", "");
                Ok("vm.import", importedId + " (" + Json.GetString(imported, "name", "") + ")");

                Dictionary<string, object> diagnostics = Call(client, "host.diagnostics", null, 300000);
                string diagnosticsPath = Json.GetString(diagnostics, "path", "");

                if (File.Exists(diagnosticsPath))
                    Ok("host.diagnostics", new FileInfo(diagnosticsPath).Length + " bytes");
                else
                    Fail("diagnostics zip was not created");

                // ---- удаление
                foreach (string victim in new[] { importedId, cloneId, id })
                    Call(client, "vm.delete", Json.Obj("id", victim), 120000);

                object remaining = CallRaw(client, "vm.list", null);
                List<object> vms = remaining as List<object>;
                Ok("vm.delete", (vms == null ? 0 : vms.Count) + " vm(s) left");

                if (vms != null && vms.Count > 0) Fail("VM was not removed");

                try { File.Delete(archive); } catch { }
            }
            catch (Exception ex)
            {
                Fail(ex.GetType().Name + ": " + ex.Message);
                Log.Exception("ITEST", "исключение", ex);
            }
            finally
            {
                try { client.Dispose(); } catch { }
                try { runner.Stop(); } catch { }

                if (keep)
                {
                    Console.WriteLine("  artifacts kept: " + root);
                }
                else
                {
                    try { if (Directory.Exists(root)) Directory.Delete(root, true); } catch { }
                }
            }

            Console.WriteLine(_failures == 0 ? "ITEST: OK" : "ITEST: FAILED (" + _failures + ")");
            return _failures == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------ помощники

        private static object CallRaw(PipeClient client, string method,
            Dictionary<string, object> parameters, int timeoutMs = 60000)
        {
            return client.CallForResultAsync(method, parameters, timeoutMs).GetAwaiter().GetResult();
        }

        private static Dictionary<string, object> Call(PipeClient client, string method,
            Dictionary<string, object> parameters, int timeoutMs = 60000)
        {
            return CallRaw(client, method, parameters, timeoutMs) as Dictionary<string, object>;
        }

        private static Dictionary<string, object> WaitState(PipeClient client, string id, string expected, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            Dictionary<string, object> last = null;

            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                last = Call(client, "vm.get", Json.Obj("id", id), 15000);
                string state = Json.GetString(last, "state", "");

                if (state == expected) return last;

                if (state == "failed")
                    throw new Hv86Exception("VM failed: " + Json.GetString(last, "last_error", ""));

                System.Threading.Thread.Sleep(300);
            }

            throw new Hv86Exception("VM did not reach '" + expected + "' in " + timeoutMs + " ms (last: " +
                                    Json.GetString(last, "state", "?") + ")");
        }

        private static void WaitInstructions(PipeClient client, string id, long minimum, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();

            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                Dictionary<string, object> view = Call(client, "vm.get", Json.Obj("id", id), 15000);

                if (Json.GetLong(view, "instructions", 0) >= minimum) return;
                if (Json.GetString(view, "state", "") == "failed") return;

                System.Threading.Thread.Sleep(300);
            }
        }

        /// <summary>Ждёт, пока vmhost сообщит порт wisp-релея.</summary>
        private static int WaitRelayPort(PipeClient client, string id, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();

            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                Dictionary<string, object> view = Call(client, "vm.get", Json.Obj("id", id), 15000);
                int port = Json.GetInt(view, "relay_port", 0);

                if (port > 0) return port;
                if (Json.GetString(view, "state", "") == "failed") break;

                System.Threading.Thread.Sleep(400);
            }

            throw new Hv86Exception("relay port did not appear");
        }

        /// <summary>Ждёт, пока к релею подключится сама страница v86.</summary>
        private static void WaitRelayClients(PipeClient client, string id, int minimum, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();

            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                Dictionary<string, object> view = Call(client, "vm.get", Json.Obj("id", id), 15000);

                if (Json.GetInt(view, "relay_clients", 0) >= minimum) return;

                System.Threading.Thread.Sleep(400);
            }

            throw new Hv86Exception("v86 never connected to the wisp relay (websocket blocked?)");
        }

        /// <summary>Проверка проброса: подключаемся как обычный клиент и ждём закрытия.</summary>
        private static string ProbeForward(string host, int port, int timeoutMs)
        {
            try
            {
                using (System.Net.Sockets.TcpClient client = new System.Net.Sockets.TcpClient())
                {
                    System.Threading.Tasks.Task connect = client.ConnectAsync(host, port);

                    if (!connect.Wait(8000)) return "connect timeout (listener did not start)";

                    client.ReceiveTimeout = timeoutMs;
                    byte[] buffer = new byte[16];

                    try
                    {
                        int read = client.GetStream().Read(buffer, 0, buffer.Length);

                        return read == 0
                            ? "closed by vmhost as expected"
                            : "unexpected " + read + " bytes from guest";
                    }
                    catch (System.IO.IOException)
                    {
                        return "closed by reset (guest has no listener)";
                    }
                }
            }
            catch (Exception ex)
            {
                return "error: " + ex.Message;
            }
        }

        /// <summary>Ждёт, пока watchdog зафиксирует падение и начнёт перезапуск.</summary>
        private static void WaitRestarts(PipeClient client, string id, int minimum, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();

            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                Dictionary<string, object> view = Call(client, "vm.get", Json.Obj("id", id), 15000);

                if (Json.GetInt(view, "restarts", 0) >= minimum) return;
                if (Json.GetString(view, "state", "") == "failed") return;

                System.Threading.Thread.Sleep(300);
            }
        }

        /// <summary>Ждёт появления чекпоинтов кольца (ft.interval_ms).</summary>
        private static void WaitCheckpoints(PipeClient client, string id, int minimum, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();

            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                Dictionary<string, object> view = Call(client, "vm.get", Json.Obj("id", id), 15000);

                if (Json.GetInt(view, "checkpoints", 0) >= minimum) return;
                if (Json.GetString(view, "state", "") == "failed") return;

                System.Threading.Thread.Sleep(400);
            }
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
