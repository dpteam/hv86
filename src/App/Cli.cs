using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Hv86.Core;

namespace Hv86.App
{
    /// <summary>
    /// `hv86.exe --cli ...` — управление гипервизором из консоли.
    ///
    /// Работает через тот же named pipe, что и интерфейс, поэтому служба
    /// остаётся единственным владельцем состояния: CLI ничего не пишет
    /// в файлы сам. `--json` отдаёт машинный вывод для скриптов.
    ///
    /// Примеры:
    ///   hv86 --cli list
    ///   hv86 --cli start tinycore-dev
    ///   hv86 --cli snapshot tinycore-dev before-update
    ///   hv86 --cli create --name dev --preset tinycore --memory 256
    /// </summary>
    public static class Cli
    {
        public static int Run(string[] args)
        {
            List<string> positional;
            Dictionary<string, string> options;
            bool json;

            try
            {
                Parse(args, out positional, out options, out json);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.Message);
                return 2;
            }

            if (positional.Count == 0 || positional[0] == "help" || positional[0] == "--help")
            {
                Usage();
                return positional.Count == 0 ? 2 : 0;
            }

            string command = positional[0].ToLowerInvariant();

            if (command == "status")
                return Status(json);

            using (PipeClient client = new PipeClient(PipeNames.Service))
            {
                if (!client.ConnectAsync(3000).GetAwaiter().GetResult())
                {
                    Console.Error.WriteLine("Служба hv86 не отвечает.");
                    Console.Error.WriteLine("Запустить: hv86.exe --service start   (или движок вручную: hv86.exe --engine)");
                    return 1;
                }

                try
                {
                    return Execute(client, command, positional, options, json);
                }
                catch (Hv86Exception ex)
                {
                    Console.Error.WriteLine("Ошибка: " + ex.Message);
                    return 1;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine("Ошибка: " + ex.GetType().Name + ": " + ex.Message);
                    return 1;
                }
            }
        }

        private static int Execute(PipeClient client, string command, List<string> positional,
            Dictionary<string, string> options, bool json)
        {
            switch (command)
            {
                case "list":
                case "ls":
                    return ListVms(client, json);

                case "presets":
                    return Presets(client, json);

                case "log":
                    return LogTail(client, options, json);

                case "diagnostics":
                    return Diagnostics(client, json);

                case "start":
                    return Simple(client, positional, "vm.start", "запущена", "running", json);

                case "stop":
                    return Simple(client, positional, "vm.stop", "остановлена", "stopped", json);

                case "pause":
                    return Simple(client, positional, "vm.pause", "на паузе", "paused", json);

                case "resume":
                    return Simple(client, positional, "vm.resume", "продолжает работу", "running", json);

                case "reset":
                    return Simple(client, positional, "vm.reset", "перезагружена", "running", json);

                case "console":
                    return ConsoleWindow(client, positional, json);

                case "screen":
                    return ScreenText(client, positional, json);

                case "delete":
                    return Delete(client, positional, json);

                case "snapshot":
                    return Snapshot(client, positional, json);

                case "snapshots":
                    return Snapshots(client, positional, json);

                case "revert":
                    return Revert(client, positional, json);

                case "type":
                case "paste":
                    return TypeText(client, positional, options, json);

                case "floppy":
                    return Floppy(client, positional, json);

                case "migrate":
                    return Migrate(client, positional, json);

                case "replicas":
                    return Replicas(client, json);

                case "create":
                    return Create(client, options, json);

                case "export":
                    return Export(client, positional, json);

                case "import":
                    return Import(client, positional, json);
            }

            Console.Error.WriteLine("Неизвестная команда: " + command);
            Usage();
            return 2;
        }

        // ------------------------------------------------------------- команды

        private static int Status(bool json)
        {
            ServiceStatus status = ServiceManager.Query();
            Dictionary<string, object> host = null;

            // движок может быть запущен вручную (--engine), поэтому пробуем канал
            using (PipeClient client = new PipeClient(PipeNames.Service))
            {
                if (client.ConnectAsync(1200).GetAwaiter().GetResult())
                {
                    try
                    {
                        host = client.CallForResultAsync("host.status", null, 5000).GetAwaiter().GetResult()
                            as Dictionary<string, object>;
                    }
                    catch
                    {
                        // движок не ответил на запрос - показываем хотя бы службу
                    }
                }
            }

            if (json)
            {
                Console.WriteLine(Json.Pretty(Json.Obj(
                    "service", Json.Obj(
                        "installed", status.Installed,
                        "running", status.Running,
                        "state", status.State,
                        "path", status.Path,
                        "problem", status.Problem),
                    "engine", host)));
                return 0;
            }

            Console.WriteLine("служба: " + status.Text());
            if (status.Path.Length > 0) Console.WriteLine("exe:    " + status.Path);
            if (status.Problem.Length > 0) Console.WriteLine("note:   " + status.Problem);

            if (host == null)
            {
                Console.WriteLine("движок: не отвечает (hv86 --service start или hv86 --engine)");
                return 0;
            }

            Console.WriteLine("движок: " + Json.GetString(host, "product", "") + " " +
                              Json.GetString(host, "version", "") + ", v86 " + Json.GetString(host, "v86", ""));
            Console.WriteLine("uptime: " + Json.GetLong(host, "uptime_sec", 0) + " c");
            Console.WriteLine("ВМ:     " + Json.GetLong(host, "vms_total", 0) + " (" +
                              Json.GetLong(host, "vms_running", 0) + " работает)");
            Console.WriteLine("данные: " + Json.GetString(host, "data_dir", ""));
            Console.WriteLine("сессия: " + Json.GetLong(host, "session", 0) +
                              ", активная: " + Json.GetLong(host, "active_session", 0));

            return 0;
        }

        private static int ListVms(PipeClient client, bool json)
        {
            List<object> vms = Result(client, "vm.list", null, 15000) as List<object> ?? new List<object>();

            if (json)
            {
                Console.WriteLine(Json.Pretty(vms));
                return 0;
            }

            if (vms.Count == 0)
            {
                Console.WriteLine("ВМ нет. Создать: hv86 --cli create --name dev --preset freedos");
                return 0;
            }

            Console.WriteLine(string.Format("{0,-10} {1,-22} {2,-12} {3,7} {4,8} {5,9} {6,8}",
                "ID", "ИМЯ", "СОСТОЯНИЕ", "ПАМЯТЬ", "MIPS", "UPTIME", "РЕСТАРТ"));

            foreach (object item in vms)
            {
                Dictionary<string, object> vm = item as Dictionary<string, object>;
                if (vm == null) continue;

                Console.WriteLine(string.Format("{0,-10} {1,-22} {2,-12} {3,7} {4,8} {5,9} {6,8}",
                    Json.GetString(vm, "id", ""),
                    Trim(Json.GetString(vm, "name", ""), 22),
                    Json.GetString(vm, "state", ""),
                    Json.GetInt(vm, "memory_mb", 0) + "M",
                    Json.GetInt(vm, "mips", 0) == 0 ? "-" : Json.GetInt(vm, "mips", 0).ToString(CultureInfo.InvariantCulture),
                    Uptime(Json.GetLong(vm, "uptime_sec", 0)),
                    Json.GetInt(vm, "restarts", 0).ToString(CultureInfo.InvariantCulture)));
            }

            return 0;
        }

        private static int Presets(PipeClient client, bool json)
        {
            List<object> presets = Result(client, "vm.presets", null, 15000) as List<object> ?? new List<object>();

            if (json)
            {
                Console.WriteLine(Json.Pretty(presets));
                return 0;
            }

            foreach (object item in presets)
            {
                Dictionary<string, object> preset = item as Dictionary<string, object>;
                if (preset == null) continue;

                Console.WriteLine(string.Format("{0,-12} {1,-22} {2,6} МБ  {3}",
                    Json.GetString(preset, "id", ""),
                    Trim(Json.GetString(preset, "name", ""), 22),
                    Json.GetInt(preset, "memory_mb", 0),
                    Json.GetString(preset, "description", "")));
            }

            return 0;
        }

        private static int LogTail(PipeClient client, Dictionary<string, string> options, bool json)
        {
            int lines = 200;
            string linesOption;
            if (options.TryGetValue("lines", out linesOption)) int.TryParse(linesOption, out lines);

            Dictionary<string, object> result = Result(client, "host.log", Json.Obj("lines", lines), 20000)
                as Dictionary<string, object>;

            string text = Json.GetString(result, "text", "");

            if (json) Console.WriteLine(Json.Pretty(Json.Obj("text", text)));
            else Console.Write(text);

            return 0;
        }

        private static int Diagnostics(PipeClient client, bool json)
        {
            Dictionary<string, object> result = Result(client, "host.diagnostics", null, 300000)
                as Dictionary<string, object>;

            if (json)
            {
                Console.WriteLine(Json.Pretty(result));
                return 0;
            }

            Console.WriteLine("диагностика: " + Json.GetString(result, "path", "") +
                              " (" + Math.Round(Json.GetLong(result, "size", 0) / 1024.0, 1) + " КБ)");
            return 0;
        }

        private static int Simple(PipeClient client, List<string> positional, string method,
            string doneText, string expectedState, bool json)
        {
            string id = ResolveId(client, Positional(positional, 1));

            Result(client, method, Json.Obj("id", id), 180000);

            if (expectedState != null) WaitState(client, id, expectedState, 120000);

            Dictionary<string, object> view = Result(client, "vm.get", Json.Obj("id", id), 15000)
                as Dictionary<string, object>;

            if (json) Console.WriteLine(Json.Pretty(view));
            else Console.WriteLine("ВМ " + Json.GetString(view, "name", id) + " " + doneText +
                                   " (" + Json.GetString(view, "state", "") + ")");

            return 0;
        }

        /// <summary>
        /// `type`/`paste`: отправить текст в гостя нажатиями. Текст собирается
        /// из всех оставшихся аргументов, поэтому пробелы не надо экранировать.
        /// </summary>
        private static int TypeText(PipeClient client, List<string> positional,
            Dictionary<string, string> options, bool json)
        {
            string id = ResolveId(client, Positional(positional, 1));

            StringBuilder text = new StringBuilder();

            for (int i = 2; i < positional.Count; i++)
            {
                if (text.Length > 0) text.Append(' ');
                text.Append(positional[i]);
            }

            bool enter = options.ContainsKey("enter");

            if (text.Length == 0 && !enter)
            {
                Console.Error.WriteLine("Нечего вводить: hv86 --cli type <vm> \"текст\" [--enter]");
                return 2;
            }

            Dictionary<string, object> result = Result(client, "vm.keys",
                Json.Obj("id", id, "text", text.ToString(), "enter", enter), 120000)
                as Dictionary<string, object>;

            if (json) Console.WriteLine(Json.Pretty(result));
            else
                Console.WriteLine("введено " + Json.GetInt(result, "chars", 0) + " символов (" +
                                  (enter ? "с Enter" : "без Enter") + ")");

            return 0;
        }

        /// <summary>
        /// `floppy <vm> [next|prev|N]`: вставить следующую/предыдущую дискету
        /// комплекта или конкретную (0-based индекс). Без аргумента - показать.
        /// </summary>
        private static int Floppy(PipeClient client, List<string> positional, bool json)
        {
            string id = ResolveId(client, Positional(positional, 1));
            string what = Positional(positional, 2);

            Dictionary<string, object> view = Result(client, "vm.get", Json.Obj("id", id), 15000)
                as Dictionary<string, object>;

            int count = Json.GetInt(view, "floppy_count", 0);

            if (string.IsNullOrEmpty(what))
            {
                if (json) Console.WriteLine(Json.Pretty(view));

                Console.WriteLine(count == 0
                    ? "у ВМ нет комплекта дискет"
                    : "дискета " + (Json.GetInt(view, "floppy_index", 0) + 1) + "/" + count +
                      ": " + Json.GetString(view, "floppy", ""));

                return count == 0 ? 1 : 0;
            }

            if (count == 0)
            {
                Console.Error.WriteLine("у ВМ нет комплекта дискет (одна дискета или ни одной)");
                return 1;
            }

            Dictionary<string, object> parameters = Json.Obj("id", id);

            if (what == "next") parameters["delta"] = 1;
            else if (what == "prev") parameters["delta"] = -1;
            else
            {
                int index;

                if (!int.TryParse(what, out index) || index < 0 || index >= count)
                {
                    Console.Error.WriteLine("нужен next, prev или индекс 0.." + (count - 1));
                    return 2;
                }

                parameters["index"] = index;
            }

            Dictionary<string, object> result = Result(client, "vm.floppy", parameters, 60000)
                as Dictionary<string, object>;

            if (json) Console.WriteLine(Json.Pretty(result));

            Console.WriteLine("вставлена дискета " + (Json.GetInt(result, "floppy_index", 0) + 1) + "/" +
                              Json.GetInt(result, "floppy_count", count) + ": " +
                              Json.GetString(result, "floppy", ""));

            return 0;
        }

        /// <summary>
        /// `migrate <vm>`: перенести ВМ на встроенный движок v86. Диски остаются,
        /// снапшоты и чекпоинты старой версии удаляются - state между версиями
        /// несовместим, и делать вид, что всё переносится, было бы обманом.
        /// </summary>
        private static int Migrate(PipeClient client, List<string> positional, bool json)
        {
            string id = ResolveId(client, Positional(positional, 1));

            Dictionary<string, object> result = Result(client, "vm.migrate", Json.Obj("id", id), 180000)
                as Dictionary<string, object>;

            if (json) Console.WriteLine(Json.Pretty(result));

            if (!Json.GetBool(result, "migrated", false))
            {
                Console.WriteLine("ВМ уже на встроенном движке v86 " +
                                  Json.GetString(result, "version", "") + " - перенос не нужен");
                return 0;
            }

            Console.WriteLine("ВМ перенесена с v86 " + Json.GetString(result, "from", "") +
                              " на " + Json.GetString(result, "to", "") +
                              "; снапшотов убрано: " + Json.GetInt(result, "snapshots", 0) +
                              ", чекпоинтов: " + Json.GetInt(result, "checkpoints", 0));

            return 0;
        }

        /// <summary>
        /// `replicas`: что этот хост принял от других машин. Команду имеет смысл
        /// запускать на реплике - на основном хосте список обычно пуст.
        /// </summary>
        private static int Replicas(PipeClient client, bool json)
        {
            List<object> items = Result(client, "host.replicas", null, 15000) as List<object>
                                 ?? new List<object>();

            if (json) Console.WriteLine(Json.Pretty(items));

            if (items.Count == 0)
            {
                Console.WriteLine("принятых реплик нет");
                return 0;
            }

            foreach (object item in items)
            {
                Dictionary<string, object> replica = item as Dictionary<string, object>;
                if (replica == null) continue;

                Console.WriteLine(Json.GetString(replica, "vm_id", "") + "  " +
                                  Json.GetString(replica, "name", "") + ": эпоха " +
                                  Json.GetInt(replica, "epoch", 0) + ", state " +
                                  Json.GetLong(replica, "seq", 0) + ", " +
                                  Json.GetLong(replica, "age_sec", 0) + " с назад" +
                                  (Json.GetBool(replica, "warm", false) ? "" : " (пусто)"));
            }

            return 0;
        }

        /// <summary>
        /// Открыть консоль. ВМ сама не запускается: окно покажет «выключена»
        /// и кнопку Start - как в Hyper-V.
        /// </summary>
        private static int ConsoleWindow(PipeClient client, List<string> positional, bool json)
        {
            string id = ResolveId(client, Positional(positional, 1));

            Dictionary<string, object> view = Result(client, "vm.console",
                Json.Obj("id", id, "start", false), 120000) as Dictionary<string, object>;

            if (json) Console.WriteLine(Json.Pretty(view));
            else
                Console.WriteLine("окно консоли открыто (ВМ: " +
                                  Json.GetString(view, "state", "") + ")");

            return 0;
        }

        /// <summary>
        /// Текст с экрана headless-ВМ: единственный способ увидеть гостя из
        /// консоли (у web-движка экран рисует WebView2, там `vm.screen` нет).
        /// </summary>
        private static int ScreenText(PipeClient client, List<string> positional, bool json)
        {
            string id = ResolveId(client, Positional(positional, 1));

            Dictionary<string, object> view = Result(client, "vm.get", Json.Obj("id", id), 15000)
                as Dictionary<string, object>;

            string pipe = Json.GetString(view, "pipe", "");

            if (pipe.Length == 0) throw new Hv86Exception("ВМ не запущена: экрана нет");

            using (PipeClient vm = new PipeClient(pipe))
            {
                if (!vm.ConnectAsync(5000).GetAwaiter().GetResult())
                    throw new Hv86Exception("движок ВМ не отвечает");

                Dictionary<string, object> screen = vm.CallForResultAsync("vm.screen", null, 15000)
                    .GetAwaiter().GetResult() as Dictionary<string, object>;

                if (json)
                {
                    Console.WriteLine(Json.Pretty(screen));
                    return 0;
                }

                List<object> lines = Json.GetList(screen, "lines");

                if (lines == null)
                {
                    Console.WriteLine("(экран недоступен: vm.screen есть только у headless-движка)");
                    return 0;
                }

                foreach (object line in lines) Console.WriteLine(AnsiText.Strip(line as string ?? ""));
            }

            return 0;
        }

        private static int Delete(PipeClient client, List<string> positional, bool json)
        {
            string id = ResolveId(client, Positional(positional, 1));
            Result(client, "vm.delete", Json.Obj("id", id), 180000);

            if (json) Console.WriteLine(Json.Pretty(Json.Obj("id", id)));
            else Console.WriteLine("ВМ " + id + " перемещена в корзину hv86");

            return 0;
        }

        private static int Snapshot(PipeClient client, List<string> positional, bool json)
        {
            string id = ResolveId(client, Positional(positional, 1));

            string name = Positional(positional, 2);
            if (string.IsNullOrEmpty(name))
                name = "cli-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);

            Dictionary<string, object> snapshot = Result(client, "vm.snapshot",
                Json.Obj("id", id, "name", name), 600000) as Dictionary<string, object>;

            if (json) Console.WriteLine(Json.Pretty(snapshot));
            else Console.WriteLine("снапшот " + Json.GetString(snapshot, "id", "") +
                                   " (" + Math.Round(Json.GetLong(snapshot, "size", 0) / 1024.0 / 1024.0, 1) + " МБ)");

            return 0;
        }

        private static int Snapshots(PipeClient client, List<string> positional, bool json)
        {
            string id = ResolveId(client, Positional(positional, 1));
            List<object> items = Result(client, "vm.snapshots", Json.Obj("id", id), 30000) as List<object>
                                 ?? new List<object>();

            if (json)
            {
                Console.WriteLine(Json.Pretty(items));
                return 0;
            }

            if (items.Count == 0) Console.WriteLine("снапшотов нет");

            foreach (object item in items)
            {
                Dictionary<string, object> snapshot = item as Dictionary<string, object>;
                if (snapshot == null) continue;

                Console.WriteLine(string.Format("{0,-24} {1,-20} {2,8}",
                    Json.GetString(snapshot, "id", ""),
                    Json.GetString(snapshot, "created_utc", ""),
                    Math.Round(Json.GetLong(snapshot, "size", 0) / 1024.0 / 1024.0, 1) + " МБ"));
            }

            return 0;
        }

        private static int Revert(PipeClient client, List<string> positional, bool json)
        {
            string id = ResolveId(client, Positional(positional, 1));
            string snapshot = Positional(positional, 2);

            if (string.IsNullOrEmpty(snapshot))
                throw new Hv86Exception("укажите id снапшота: hv86 --cli snapshots <vm>");

            Dictionary<string, object> view = Result(client, "vm.revert",
                Json.Obj("id", id, "snapshot", snapshot), 600000) as Dictionary<string, object>;

            if (json) Console.WriteLine(Json.Pretty(view));
            else Console.WriteLine("ВМ " + Json.GetString(view, "name", id) + " откачена к " + snapshot);

            return 0;
        }

        private static int Create(PipeClient client, Dictionary<string, string> options, bool json)
        {
            Dictionary<string, object> parameters = Json.Obj("name", Option(options, "name", ""));

            string preset = Option(options, "preset", "");
            if (!string.IsNullOrEmpty(preset)) parameters["preset"] = preset;

            string iso = Option(options, "iso", "");
            if (!string.IsNullOrEmpty(iso)) parameters["iso_path"] = Path.GetFullPath(iso);

            int memory;
            if (int.TryParse(Option(options, "memory", ""), out memory)) parameters["memory_mb"] = memory;

            int disk;
            if (int.TryParse(Option(options, "disk", ""), out disk)) parameters["disk_mb"] = disk;

            if (options.ContainsKey("autostart")) parameters["autostart"] = true;

            string netMode = Option(options, "net", "");
            if (netMode == "relay") parameters["net_mode"] = "relay";

            string engine = Option(options, "engine", "");
            if (engine == "headless") parameters["engine"] = "headless";

            string share = Option(options, "share", "");
            if (!string.IsNullOrEmpty(share)) parameters["share_path"] = share;

            if (string.IsNullOrEmpty(preset) && string.IsNullOrEmpty(iso))
                throw new Hv86Exception("нужен --preset <id> или --iso <путь>");

            Dictionary<string, object> view = Result(client, "vm.create", parameters, 900000)
                as Dictionary<string, object>;

            if (json) Console.WriteLine(Json.Pretty(view));
            else Console.WriteLine("создана ВМ " + Json.GetString(view, "id", "") + " (" +
                                   Json.GetString(view, "name", "") + ")");

            return 0;
        }

        private static int Export(PipeClient client, List<string> positional, bool json)
        {
            string id = ResolveId(client, Positional(positional, 1));
            string path = Positional(positional, 2);

            if (string.IsNullOrEmpty(path))
                path = Path.Combine(Environment.CurrentDirectory,
                    SafeName(Json.GetString(Result(client, "vm.get", Json.Obj("id", id), 15000)
                        as Dictionary<string, object>, "name", id)) + ".hv86x");

            Dictionary<string, object> result = Result(client, "vm.export",
                Json.Obj("id", id, "path", Path.GetFullPath(path)), 1800000) as Dictionary<string, object>;

            if (json) Console.WriteLine(Json.Pretty(result));
            else Console.WriteLine("экспорт: " + Json.GetString(result, "path", "") +
                                   " (" + Math.Round(Json.GetLong(result, "size", 0) / 1024.0 / 1024.0, 1) + " МБ)");

            return 0;
        }

        private static int Import(PipeClient client, List<string> positional, bool json)
        {
            string path = Positional(positional, 1);
            if (string.IsNullOrEmpty(path)) throw new Hv86Exception("укажите файл .hv86x");

            Dictionary<string, object> view = Result(client, "vm.import",
                Json.Obj("path", Path.GetFullPath(path)), 1800000) as Dictionary<string, object>;

            if (json) Console.WriteLine(Json.Pretty(view));
            else Console.WriteLine("импортирована ВМ " + Json.GetString(view, "id", "") +
                                   " (" + Json.GetString(view, "name", "") + ")");

            return 0;
        }

        // ------------------------------------------------------------- утилиты

        private static object Result(PipeClient client, string method, Dictionary<string, object> parameters,
            int timeoutMs)
        {
            return client.CallForResultAsync(method, parameters, timeoutMs).GetAwaiter().GetResult();
        }

        /// <summary>Ищет ВМ по id, префиксу id или имени. Неоднозначность - ошибка.</summary>
        private static string ResolveId(PipeClient client, string needle)
        {
            if (string.IsNullOrEmpty(needle))
                throw new Hv86Exception("укажите ВМ: hv86 --cli list");

            List<object> vms = Result(client, "vm.list", null, 15000) as List<object> ?? new List<object>();
            List<Dictionary<string, object>> matches = new List<Dictionary<string, object>>();

            foreach (object item in vms)
            {
                Dictionary<string, object> vm = item as Dictionary<string, object>;
                if (vm == null) continue;

                string id = Json.GetString(vm, "id", "");
                string name = Json.GetString(vm, "name", "");

                if (id == needle ||
                    id.StartsWith(needle, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(name, needle, StringComparison.CurrentCultureIgnoreCase))
                {
                    matches.Add(vm);
                }
            }

            if (matches.Count == 1) return Json.GetString(matches[0], "id", "");

            if (matches.Count == 0) throw new Hv86Exception("ВМ не найдена: " + needle);
            throw new Hv86Exception("под '" + needle + "' подходит несколько ВМ — уточните id");
        }

        private static void WaitState(PipeClient client, string id, string expected, int timeoutMs)
        {
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                Dictionary<string, object> view = Result(client, "vm.get", Json.Obj("id", id), 15000)
                    as Dictionary<string, object>;

                string state = Json.GetString(view, "state", "");

                if (state == expected) return;
                if (state == "failed") throw new Hv86Exception("ВМ упала: " + Json.GetString(view, "last_error", ""));

                System.Threading.Thread.Sleep(300);
            }

            throw new Hv86Exception("ВМ не перешла в состояние " + expected + " за " + timeoutMs + " мс");
        }

        private static void Parse(string[] args, out List<string> positional,
            out Dictionary<string, string> options, out bool json)
        {
            positional = new List<string>();
            options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            json = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];

                if (string.Equals(arg, "--json", StringComparison.OrdinalIgnoreCase))
                {
                    json = true;
                    continue;
                }

                if (arg.StartsWith("--", StringComparison.Ordinal))
                {
                    string key = arg.Substring(2);

                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        options[key] = args[i + 1];
                        i++;
                    }
                    else
                    {
                        options[key] = "true";
                    }

                    continue;
                }

                positional.Add(arg);
            }
        }

        private static string Option(Dictionary<string, string> options, string key, string fallback)
        {
            string value;
            return options.TryGetValue(key, out value) ? value : fallback;
        }

        private static string Positional(List<string> positional, int index)
        {
            return index < positional.Count ? positional[index] : null;
        }

        private static string Trim(string text, int length)
        {
            if (string.IsNullOrEmpty(text)) return "";
            return text.Length <= length ? text : text.Substring(0, length - 1) + "…";
        }

        private static string Uptime(long seconds)
        {
            if (seconds <= 0) return "-";

            long h = seconds / 3600;
            long m = seconds % 3600 / 60;

            return h > 0 ? h + "ч" + m.ToString("00", CultureInfo.InvariantCulture) + "м"
                         : m + "м";
        }

        private static string SafeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name)) return "vm";

            StringBuilder sb = new StringBuilder();
            foreach (char c in name.Trim())
                sb.Append(Path.GetInvalidFileNameChars().Contains(c) ? '_' : c);

            return sb.ToString();
        }

        private static void Usage()
        {
            Console.WriteLine("hv86 — управление виртуальными машинами из консоли");
            Console.WriteLine();
            Console.WriteLine("  hv86 --cli list                      список ВМ");
            Console.WriteLine("  hv86 --cli status                    состояние службы и движка");
            Console.WriteLine("  hv86 --cli start|stop|pause|resume|reset|console <vm>");
            Console.WriteLine("  hv86 --cli delete <vm>");
            Console.WriteLine("  hv86 --cli snapshot <vm> [имя]       снять снапшот");
            Console.WriteLine("  hv86 --cli snapshots <vm>            список снапшотов");
            Console.WriteLine("  hv86 --cli revert <vm> <снапшот>     откатить");
            Console.WriteLine("  hv86 --cli type <vm> \"текст\" [--enter]   набрать в госте");
            Console.WriteLine("  hv86 --cli floppy <vm> [next|prev|N]     сменить дискету комплекта");
            Console.WriteLine("  hv86 --cli migrate <vm>                  перенести ВМ на встроенный v86");
            Console.WriteLine("  hv86 --cli replicas                     реплики, принятые этим хостом");
            Console.WriteLine("  hv86 --cli screen <vm>               текст с экрана (headless)");
            Console.WriteLine("  hv86 --cli create --name <имя> --preset <id> [--memory МБ] [--disk МБ] [--net relay] [--engine headless] [--share C:\\dir]");
            Console.WriteLine("  hv86 --cli create --name <имя> --iso <файл>");
            Console.WriteLine("  hv86 --cli export <vm> [файл.hv86x]");
            Console.WriteLine("  hv86 --cli import <файл.hv86x>");
            Console.WriteLine("  hv86 --cli presets                   доступные образы");
            Console.WriteLine("  hv86 --cli log [--lines N]           хвост лога");
            Console.WriteLine("  hv86 --cli diagnostics               zip с логами и описанием ВМ");
            Console.WriteLine();
            Console.WriteLine("  <vm> — id, его префикс или имя. Флаг --json печатает JSON.");
        }
    }
}