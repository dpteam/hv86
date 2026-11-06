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

                // ввод в гостя в web-движке: служба просит страницу набрать
                // текст, страница превращает его в скан-коды и подтверждает
                Dictionary<string, object> typed = null;

                using (PipeClient vmPipe = new PipeClient(PipeNames.VmHost(id)))
                {
                    if (!vmPipe.ConnectAsync(5000).GetAwaiter().GetResult())
                        Fail("cannot connect to the vm pipe for the typing probe");
                    else
                        typed = vmPipe.CallForResultAsync("vm.key",
                                Json.Obj("text", "echo hv86web\n"), 90000).GetAwaiter().GetResult()
                            as Dictionary<string, object>;
                }

                if (typed != null && Json.GetBool(typed, "ok", false) && Json.GetInt(typed, "chars", 0) > 0)
                    Ok("web keyboard input", "page typed " + Json.GetInt(typed, "chars", 0) + " chars into the guest");
                else
                    Fail("web keyboard input failed: " + Json.Stringify(typed));

                // масштаб экрана консоли: 1x / 2x / fit (через zoom контейнера)
                Dictionary<string, object> size1 = null;

                // экран мог быть ещё пустым (гость грузится): ждём, пока у
                // текстового слоя появится высота, иначе fit считать не из чего
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    size1 = CallVm(id, "vm.debug.scale", Json.Obj("mode", "1"), 30000);
                    if (Json.GetInt(size1, "height", 0) > 0 && Json.GetInt(size1, "width", 0) > 0) break;

                    System.Threading.Thread.Sleep(500);
                }

                Dictionary<string, object> size2 = CallVm(id, "vm.debug.scale", Json.Obj("mode", "2"), 30000);

                int w1 = Json.GetInt(size1, "width", 0);
                int w2 = Json.GetInt(size2, "width", 0);

                if (w1 > 0 && w2 >= w1 * 2 - 4)
                    Ok("console scale", "1x=" + w1 + "px, 2x=" + w2 + "px");
                else
                    Fail("console scale did not apply: " + Json.Stringify(size1) + " -> " + Json.Stringify(size2));

                Dictionary<string, object> fit = CallVm(id, "vm.debug.scale", Json.Obj("mode", "fit"), 30000);

                double fitValue = 0;
                if (fit != null && fit.ContainsKey("scale"))
                    double.TryParse(Json.Stringify(fit["scale"]).Trim('"'),
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out fitValue);

                // fit обязан дать дробный масштаб (окно не кратно экрану),
                // иначе это не fit, а обычный 1x/2x
                if (fit != null && Json.GetInt(fit, "width", 0) > 0 && fitValue > 0.1 && fitValue < 1.99)
                    Ok("console fit", "fit scale " +
                        fitValue.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) +
                        ", " + Json.GetInt(fit, "width", 0) + "px inside " + Json.GetString(fit, "wrap", ""));
                else
                    Fail("console fit did not compute a scale: " + Json.Stringify(fit));

                CallVm(id, "vm.debug.scale", Json.Obj("mode", "1"), 30000);

                // шрифт текстового режима: per-VM, применяется на лету
                Call(client, "vm.update", Json.Obj("id", id,
                    "text_font", "Consolas", "text_font_size", 16), 30000);

                System.Threading.Thread.Sleep(700);

                Dictionary<string, object> fontProbe = CallVm(id, "vm.debug.scale", Json.Obj("mode", "1"), 30000);
                string fontFamily = Json.GetString(fontProbe, "font", "");
                string fontSize = Json.GetString(fontProbe, "fontSize", "");

                if (fontFamily.IndexOf("Consolas", StringComparison.OrdinalIgnoreCase) >= 0 &&
                    fontSize.StartsWith("16", StringComparison.Ordinal))
                    Ok("console font", fontFamily + " " + fontSize);
                else
                    Fail("text font was not applied in the page: " + Json.Stringify(fontProbe));

                Call(client, "vm.update", Json.Obj("id", id,
                    "text_font", "", "text_font_size", 0), 30000);

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

                // ---- горячая реплика: падение основной не должно ронять ВМ
                Call(client, "vm.update", Json.Obj("id", id,
                    "ft_mode", "replica", "ft_interval_ms", 3000), 30000);

                Call(client, "vm.stop", Json.Obj("id", id), 120000);
                WaitState(client, id, "stopped", 60000);

                Call(client, "vm.start", Json.Obj("id", id));
                WaitState(client, id, "running", 90000);

                string replicaState = WaitReplica(client, id, "warm", 180000);

                if (replicaState == "warm")
                    Ok("ft replica", "second process is warm and paused");
                else
                    Fail("replica did not become warm: '" + replicaState + "'");

                // убиваем основную: служба обязана повысить вторую, а не грузить
                // ВМ заново с чекпоинта
                Call(client, "vm.debug.kill", Json.Obj("id", id));

                Dictionary<string, object> promoted = WaitPipe(client, id, "-r", 120000);
                string promotedPipe = Json.GetString(promoted, "pipe", "");

                if (promotedPipe.EndsWith("-r", StringComparison.Ordinal) &&
                    Json.GetString(promoted, "state", "") == "running")
                    Ok("replica promoted", "pipe=" + promotedPipe + ", restarts=" +
                        Json.GetInt(promoted, "restarts", 0));
                else
                    Fail("VM did not continue on the replica: " + Json.Stringify(promoted));

                bool promotedEvent = false;
                lock (events)
                {
                    for (int i = 0; i < events.Count; i++)
                        if (events[i].IndexOf("Реплика повышена", StringComparison.Ordinal) >= 0)
                            promotedEvent = true;
                }

                if (!promotedEvent) Fail("no promotion event was reported");
                else Ok("promotion event", "service reported the replica takeover");

                // дальше по тесту реплика не нужна: обычные чекпоинты
                Call(client, "vm.update", Json.Obj("id", id,
                    "ft_mode", "checkpoint", "ft_interval_ms", 20000), 30000);

                // ---- сеть: relay (WISP) и произвольный TCP из гостя
                // поднимаем локальный echo-сервер и просим relay соединиться с ним
                int relayPort = 0;

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

                    relayPort = WaitRelayPort(client, id, 60000);
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

                    // ---- та же политика для fetch-бэкенда (nat): страница
                    // спрашивает разрешение у хоста, а не ходит куда попало
                    using (System.Net.Sockets.TcpListener http = new System.Net.Sockets.TcpListener(
                               System.Net.IPAddress.Loopback, 0))
                    {
                        http.Start();
                        int httpPort = ((System.Net.IPEndPoint)http.LocalEndpoint).Port;

                        System.Threading.Tasks.Task httpTask = System.Threading.Tasks.Task.Run(async delegate
                        {
                            try
                            {
                                using (System.Net.Sockets.TcpClient peer = await http.AcceptTcpClientAsync())
                                using (System.Net.Sockets.NetworkStream stream = peer.GetStream())
                                {
                                    byte[] buffer = new byte[1024];
                                    int read = await stream.ReadAsync(buffer, 0, buffer.Length);

                                    if (read <= 0) return;

                                    byte[] response = System.Text.Encoding.ASCII.GetBytes(
                                        "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n" +
                                        "Access-Control-Allow-Origin: *\r\nConnection: close\r\n\r\nok");

                                    await stream.WriteAsync(response, 0, response.Length);
                                }
                            }
                            catch
                            {
                                // тест сам решит, что делать
                            }
                        });

                        string probeUrl = "http://127.0.0.1:" + httpPort + "/";

                        // egress_mode=safe выставлен выше: loopback должен быть закрыт
                        Dictionary<string, object> deniedFetch = CallVm(id, "vm.debug.fetch",
                            Json.Obj("url", probeUrl), 60000);

                        string denyError = Json.GetString(deniedFetch, "error", "");

                        if (!Json.GetBool(deniedFetch, "ok", true) &&
                            denyError.IndexOf("egress", StringComparison.OrdinalIgnoreCase) >= 0)
                            Ok("fetch egress deny", "loopback blocked: " + denyError);
                        else
                            Fail("fetch egress did not block loopback: " + Json.Stringify(deniedFetch));

                        // и тот же адрес обязан пройти, когда политика это разрешает
                        Call(client, "vm.update", Json.Obj("id", id, "egress_mode", "any"), 30000);
                        System.Threading.Thread.Sleep(500);

                        Dictionary<string, object> allowedFetch = CallVm(id, "vm.debug.fetch",
                            Json.Obj("url", probeUrl), 60000);

                        if (Json.GetBool(allowedFetch, "ok", false) &&
                            Json.GetInt(allowedFetch, "status", 0) == 200)
                            Ok("fetch egress allow", "same address allowed in 'any' mode");
                        else
                            Fail("fetch egress blocked in 'any' mode: " + Json.Stringify(allowedFetch));

                        try { http.Stop(); } catch { }
                    }

                    try { echo.Stop(); } catch { }
                }

                // ---- реплика в режиме relay: после повышения она обязана
                // занять тот же порт релея - иначе адрес в state смотрит в никуда
                using (System.Net.Sockets.TcpListener echo2 = new System.Net.Sockets.TcpListener(
                           System.Net.IPAddress.Loopback, 0))
                {
                    echo2.Start();
                    int echo2Port = ((System.Net.IPEndPoint)echo2.LocalEndpoint).Port;

                    System.Threading.Tasks.Task echo2Task = System.Threading.Tasks.Task.Run(async delegate
                    {
                        try
                        {
                            using (System.Net.Sockets.TcpClient peer = await echo2.AcceptTcpClientAsync())
                            using (System.Net.Sockets.NetworkStream stream = peer.GetStream())
                            {
                                byte[] buffer = new byte[256];
                                int read = await stream.ReadAsync(buffer, 0, buffer.Length);
                                if (read > 0) await stream.WriteAsync(buffer, 0, read);
                            }
                        }
                        catch
                        {
                            // тест сам решит
                        }
                    });

                    Call(client, "vm.update", Json.Obj("id", id,
                        "egress_mode", "any", "ft_mode", "replica", "ft_interval_ms", 3000), 30000);

                    string relayReplica = WaitReplica(client, id, "warm", 180000);

                    if (relayReplica == "warm") Ok("relay replica", "warm, holds the relay port");
                    else Fail("relay replica did not become warm: '" + relayReplica + "'");

                    Call(client, "vm.debug.kill", Json.Obj("id", id));
                    Dictionary<string, object> relayPromoted = WaitPipe(client, id, "-r", 120000);

                    if (Json.GetString(relayPromoted, "state", "") == "running")
                        Ok("relay promotion", "VM continued on the replica after a crash");
                    else
                        Fail("relay promotion did not happen: " + Json.Stringify(relayPromoted));

                    // проверяем именно релей: echo должен пройти через тот же порт
                    string promotedDetail;
                    bool promotedOk = WispTestClient.Echo(relayPort, echo2Port, "hv86-promoted", out promotedDetail);

                    if (promotedOk) Ok("promoted relay TCP", promotedDetail);
                    else Fail("relay died with the primary: " + promotedDetail);

                    try { echo2.Stop(); } catch { }
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

                // ---- подключение к выключенной ВМ: окно открывается, гостя внутри нет,
                // а запуск грузит гостя в это же окно (как в Hyper-V)
                Dictionary<string, object> idleVm = Call(client, "vm.create", Json.Obj(
                    "name", "itest-idle", "preset", "freedos", "memory_mb", 32, "disk_mb", 0), 300000);

                string idleId = Json.GetString(idleVm, "id", "");

                Call(client, "vm.console", Json.Obj("id", idleId, "start", false), 180000);
                System.Threading.Thread.Sleep(500);

                Dictionary<string, object> idleView = Call(client, "vm.get", Json.Obj("id", idleId));

                if (Json.GetString(idleView, "state", "") == "stopped" && Json.GetBool(idleView, "idle", false))
                    Ok("console on a stopped VM", "window opened, guest not booted");
                else
                    Fail("console start:false did not open an idle window: " + Json.Stringify(idleView));

                int idlePid = Json.GetInt(CallVm(idleId, "vm.status", null), "pid", 0);

                Call(client, "vm.start", Json.Obj("id", idleId), 180000);
                WaitState(client, idleId, "running", 180000);

                int runningPid = Json.GetInt(CallVm(idleId, "vm.status", null), "pid", 0);

                if (idlePid > 0 && runningPid == idlePid)
                    Ok("boot into the open window", "same process, pid " + runningPid);
                else
                    Fail("VM was started in a new process instead of the idle window: " +
                         idlePid + " -> " + runningPid);

                Call(client, "vm.stop", Json.Obj("id", idleId), 180000);
                WaitState(client, idleId, "stopped", 60000);

                // ---- удаление
                foreach (string victim in new[] { importedId, cloneId, id, idleId })
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

        /// <summary>Запрос в канал самого vmhost: команды, которых нет у службы.</summary>
        private static Dictionary<string, object> CallVm(string id, string method,
            Dictionary<string, object> parameters, int timeoutMs = 30000)
        {
            using (PipeClient vm = new PipeClient(PipeNames.VmHost(id)))
            {
                if (!vm.ConnectAsync(5000).GetAwaiter().GetResult())
                    throw new Hv86Exception("cannot connect to the vm pipe: " + method);

                return vm.CallForResultAsync(method, parameters, timeoutMs).GetAwaiter().GetResult()
                    as Dictionary<string, object>;
            }
        }

        /// <summary>Ждёт, пока реплика прогреется ("warm") или отвалится.</summary>
        private static string WaitReplica(PipeClient client, string id, string expected, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            string last = "";

            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                Dictionary<string, object> view = Call(client, "vm.get", Json.Obj("id", id), 15000);
                last = Json.GetString(view, "replica", "");

                if (last == expected) return last;
                if (Json.GetString(view, "state", "") == "failed") return last;

                System.Threading.Thread.Sleep(400);
            }

            return last;
        }

        /// <summary>Ждёт, пока служба переключит ВМ на другой канал (повышение реплики).</summary>
        private static Dictionary<string, object> WaitPipe(PipeClient client, string id, string suffix, int timeoutMs)
        {
            Stopwatch watch = Stopwatch.StartNew();
            Dictionary<string, object> last = null;

            while (watch.ElapsedMilliseconds < timeoutMs)
            {
                last = Call(client, "vm.get", Json.Obj("id", id), 15000);
                string pipe = Json.GetString(last, "pipe", "");

                if (pipe.EndsWith(suffix, StringComparison.Ordinal)) return last;
                if (Json.GetString(last, "state", "") == "failed") return last;

                System.Threading.Thread.Sleep(300);
            }

            return last;
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
