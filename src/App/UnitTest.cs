using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using Hv86.Core;
using Hv86.Vm;
using Hv86.VmHost;

namespace Hv86.App
{
    /// <summary>
    /// `hv86.exe --unit`: микротесты того, что нельзя проверить глазами.
    ///
    /// Главный здесь - разбор state-файла v86 и применение «грязных» блоков
    /// диска: именно на этом держится сохранение диска при остановке ВМ, и
    /// ошибка тут тихо приводит к потере данных гостя.
    /// </summary>
    public static class UnitTest
    {
        private static int _failures;

        public static int Run()
        {
            Console.WriteLine("hv86 unit tests");

            string dir = Path.Combine(Path.GetTempPath(), "hv86-unit-" + DateTime.Now.ToString("HHmmss"));
            Directory.CreateDirectory(dir);

            try
            {
                TestJson();
                TestStateBlocks(dir);
                TestPaths(dir);
                TestAnsi();
                TestEgress();
                TestEgressGate();
                TestPresets();
                TestHostSettings();
                TestImagePack(dir);
                TestShare(dir);
            }
            catch (Exception ex)
            {
                Fail("unexpected exception: " + ex);
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }

            Console.WriteLine(_failures == 0 ? "UNIT: OK" : "UNIT: FAILED (" + _failures + ")");
            return _failures == 0 ? 0 : 1;
        }

        private static void TestJson()
        {
            Dictionary<string, object> source = Json.Obj(
                "id", "abc",
                "n", 42L,
                "flag", true,
                "nested", Json.Obj("x", 0.5),
                "list", Json.List(1L, "two", null));

            string text = Json.Stringify(source);
            Dictionary<string, object> back = Json.ParseObject(text);

            Check(back != null, "json: parse");
            Check(Json.GetString(back, "id", "") == "abc", "json: string");
            Check(Json.GetLong(back, "n", 0) == 42, "json: long");
            Check(Json.GetBool(back, "flag", false), "json: bool");
            Check(Json.GetObject(back, "nested") != null, "json: nested object");
            Check((Json.GetList(back, "list") ?? new List<object>()).Count == 3, "json: list");
        }

        /// <summary>
        /// Собираем синтетический state-файл ровно по формату v86:
        /// заголовок, JSON с buffer_infos, затем буферы (по 256 байт - блоки диска).
        /// </summary>
        private static void TestStateBlocks(string dir)
        {
            string statePath = Path.Combine(dir, "state.bin");
            string diskPath = Path.Combine(dir, "disk.img");

            byte[] blockA = new byte[256];
            byte[] blockB = new byte[256];
            for (int i = 0; i < 256; i++) { blockA[i] = 0xAA; blockB[i] = 0xBB; }

            string json =
                "{\"buffer_infos\":[{\"offset\":0,\"length\":256},{\"offset\":256,\"length\":256}]," +
                "\"state\":[" +
                    "[[0,{\"__state_type__\":\"Uint8Array\",\"buffer_id\":0}]," +
                    "[5,{\"__state_type__\":\"Uint8Array\",\"buffer_id\":1}]]" +
                "]}";

            byte[] jsonBytes = Encoding.UTF8.GetBytes(json);
            int buffersStart = (16 + jsonBytes.Length + 3) & ~3;
            int total = buffersStart + 512;

            byte[] file = new byte[total];
            BitConverter.GetBytes(V86State.Magic).CopyTo(file, 0);
            BitConverter.GetBytes(6).CopyTo(file, 4);
            BitConverter.GetBytes(total).CopyTo(file, 8);
            BitConverter.GetBytes(jsonBytes.Length).CopyTo(file, 12);
            jsonBytes.CopyTo(file, 16);
            blockA.CopyTo(file, buffersStart);
            blockB.CopyTo(file, buffersStart + 256);

            File.WriteAllBytes(statePath, file);

            // ---- разбор
            try
            {
                V86State.Info info = V86State.Read(statePath);
                Check(info.Lengths.Count == 2, "state: buffer_infos parsed");
                Check(V86State.CountDiskBlocks(statePath) == 2, "state: two disk blocks found");
            }
            catch (Exception ex)
            {
                Fail("state: read failed - " + ex.Message);
                return;
            }

            // ---- применение
            byte[] disk = new byte[4096];
            File.WriteAllBytes(diskPath, disk);

            int applied = V86State.ApplyDiskBlocks(statePath, diskPath);
            byte[] after = File.ReadAllBytes(diskPath);

            Check(applied == 2, "disk: two blocks applied (got " + applied + ")");
            Check(after[0] == 0xAA && after[255] == 0xAA, "disk: block 0 written");
            Check(after[1280] == 0xBB && after[1535] == 0xBB, "disk: block 5 written (index*256)");
            Check(after[256] == 0x00, "disk: untouched block stays zero");

            // ---- битый файл не должен притворяться валидным
            byte[] broken = (byte[])file.Clone();
            broken[0] ^= 0xFF;
            File.WriteAllBytes(statePath, broken);

            try
            {
                V86State.Read(statePath);
                Fail("state: broken magic accepted");
            }
            catch (Hv86Exception)
            {
                Check(true, "state: broken magic rejected");
            }
        }

        private static void TestPaths(string dir)
        {
            Paths.EnsureVmDirectories("0000abcd");

            string absolute = Path.Combine(Paths.VmDisksDir("0000abcd"), "system.img");
            string relative = Paths.Relative("0000abcd", absolute);

            Check(relative != null && relative.IndexOf(':') < 0, "paths: media stored relative");
            Check(Paths.Absolute("0000abcd", relative) == absolute, "paths: relative round-trip");

            Check(!VmStore.IsValidId("../etc"), "paths: id rejects traversal");
            Check(!VmStore.IsValidId("zzzz"), "paths: id rejects non-hex");
        }

        private static void TestAnsi()
        {
            // так v86 отдаёт экран в headless-режиме: текст + truecolor SGR
            List<AnsiRun> runs = AnsiText.Parse("A\u001b[48;2;0;0;170mB\u001b[0mC", 80);

            Check(runs.Count == 3, "ansi: three runs");
            Check(runs.Count > 0 && runs[0].Text == "A", "ansi: first run text");
            Check(runs.Count > 1 && runs[1].Text == "B", "ansi: colored run text");
            Check(runs.Count > 1 && runs[1].Background == Color.FromArgb(0, 0, 170), "ansi: background parsed");
            Check(runs.Count > 2 && runs[2].Foreground == AnsiText.DefaultForeground, "ansi: reset restores colour");
            Check(AnsiText.Strip("A\u001b[38;2;1;2;3mB\u001b[0m") == "AB", "ansi: strip");
            Check(AnsiText.Parse("\u001b[0m", 80).Count == 0, "ansi: empty line has no runs");
        }

        private static List<IPAddress> Addrs(params string[] items)
        {
            List<IPAddress> list = new List<IPAddress>();
            foreach (string item in items) list.Add(IPAddress.Parse(item));
            return list;
        }

        private static void TestEgress()
        {
            EgressPolicy safe = new EgressPolicy { Mode = EgressPolicy.ModeSafe };

            Check(safe.IsAllowed("example.com", Addrs("8.8.8.8"), 443), "egress: safe allows internet");
            Check(!safe.IsAllowed("localhost", Addrs("127.0.0.1"), 80), "egress: safe denies loopback");
            Check(!safe.IsAllowed("nas.lan", Addrs("192.168.1.10"), 445), "egress: safe denies LAN");
            Check(!safe.IsAllowed("intra", Addrs("10.1.2.3"), 22), "egress: safe denies 10/8");
            Check(!safe.IsAllowed("intra", Addrs("172.20.1.1"), 22), "egress: safe denies 172.16/12");
            Check(!safe.IsAllowed("metadata", Addrs("169.254.169.254"), 80), "egress: safe denies link-local");
            Check(!safe.IsAllowed("cgnat", Addrs("100.64.0.1"), 22), "egress: safe denies CGNAT");
            Check(!safe.IsAllowed("v6loop", Addrs("::1"), 80), "egress: safe denies IPv6 loopback");
            Check(!safe.IsAllowed("mixed", Addrs("8.8.8.8", "127.0.0.1"), 443), "egress: safe denies when any address is private");

            int dropped;

            EgressPolicy list = new EgressPolicy { Mode = EgressPolicy.ModeAllowList };
            list.Allow = EgressPolicy.ParseRules(new[]
            {
                "github.com", "10.0.0.0/8:22", "1.2.3.4:443", "*:8080"
            }, out dropped);

            Check(dropped == 0 && list.Allow.Count == 4, "egress: rules parsed");
            Check(list.IsAllowed("github.com", Addrs("140.82.121.3"), 443), "egress: allowlist host");
            Check(list.IsAllowed("api.github.com", Addrs("140.82.121.6"), 443), "egress: allowlist subdomain");
            Check(!list.IsAllowed("example.com", Addrs("93.184.216.34"), 443), "egress: allowlist denies others");
            Check(list.IsAllowed("intra", Addrs("10.9.8.7"), 22), "egress: allowlist CIDR + port");
            Check(!list.IsAllowed("intra", Addrs("10.9.8.7"), 80), "egress: allowlist respects port");
            Check(list.IsAllowed("x", Addrs("1.2.3.4"), 443), "egress: allowlist exact IP");
            Check(!list.IsAllowed("x", Addrs("1.2.3.5"), 443), "egress: allowlist other IP");
            Check(list.IsAllowed("anything.example", Addrs("8.8.4.4"), 8080), "egress: wildcard host with port");
            Check(!list.IsAllowed("anything.example", Addrs("8.8.4.4"), 8081), "egress: wildcard respects port");

            EgressPolicy empty = new EgressPolicy { Mode = EgressPolicy.ModeAllowList };
            Check(!empty.IsAllowed("example.com", Addrs("8.8.8.8"), 443), "egress: empty allowlist denies");

            int bad;
            EgressPolicy.ParseRules(new[] { "host:", "1.2.3.4/99", "", "http://x", "ok.example" }, out bad);
            Check(bad == 4, "egress: invalid rules dropped");

            Check(EgressPolicy.NormalizeMode("SAFE") == EgressPolicy.ModeSafe, "egress: mode normalization");
            Check(new EgressPolicy().IsAllowed("anywhere", Addrs("127.0.0.1"), 1), "egress: default mode is unrestricted");
        }

        /// <summary>
        /// EgressGate: то, что решает за fetch-бэкенд. Проверяем на IP-литералах,
        /// чтобы не зависеть от DNS (он здесь только для имён).
        /// </summary>
        private static void TestEgressGate()
        {
            EgressGate gate = new EgressGate(new EgressPolicy());

            Check(Json.GetBool(gate.Decide("http://192.0.2.10/x"), "allow", false), "gate: any allows http");
            Check(Json.GetBool(gate.Decide("data:text/plain,x"), "allow", false), "gate: non-http passes");
            Check(!Json.GetBool(gate.Decide("не url"), "allow", true), "gate: bad url is denied");

            EgressPolicy safe = new EgressPolicy { Mode = EgressPolicy.ModeSafe };
            gate.SetPolicy(safe);

            Dictionary<string, object> loopback = gate.Decide("http://127.0.0.1:8080/");

            Check(!Json.GetBool(loopback, "allow", true), "gate: safe denies loopback");
            Check(Json.GetString(loopback, "reason", "").IndexOf("loopback", StringComparison.Ordinal) >= 0,
                "gate: deny reason is explicit");
            Check(!Json.GetBool(gate.Decide("http://[::1]/"), "allow", true), "gate: safe denies ipv6 loopback");
            Check(Json.GetBool(gate.Decide("https://192.0.2.10/"), "allow", false), "gate: safe allows public ip");

            // кэш: то же решение отдаётся без повторного разбора, а смена
            // политики его сбрасывает
            Check(!Json.GetBool(gate.Decide("http://10.1.2.3/"), "allow", true), "gate: safe denies 10/8");

            EgressPolicy list = new EgressPolicy { Mode = EgressPolicy.ModeAllowList };
            int dropped;
            list.Allow = EgressPolicy.ParseRules(new List<string> { "192.0.2.77:443" }, out dropped);
            gate.SetPolicy(list);

            Check(Json.GetBool(gate.Decide("https://192.0.2.77/"), "allow", false),
                "gate: allowlist matches ip and port");
            Check(!Json.GetBool(gate.Decide("https://192.0.2.77:80/"), "allow", true),
                "gate: allowlist respects port");
            Check(!Json.GetBool(gate.Decide("https://192.0.2.78/"), "allow", true),
                "gate: allowlist denies other ip");
        }

        /// <summary>
        /// Каталог пресетов: два источника (свои + copy.sh), id не пересекаются,
        /// у каждой записи есть url/медиа/загрузка/память, а свои пресеты
        /// обязательно с закреплённым SHA-256.
        /// </summary>
        private static void TestPresets()
        {
            List<Dictionary<string, object>> presets = PresetCatalog.All();

            HashSet<string> ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int ownCount = 0, copyCount = 0, oldCount = 0, bad = 0;
            bool ownHashed = true;

            foreach (Dictionary<string, object> preset in presets)
            {
                string id = Json.GetString(preset, "id", "");
                string url = Json.GetString(preset, "url", "");
                string kind = Json.GetString(preset, "kind", "");
                string boot = Json.GetString(preset, "boot", "");
                string source = Json.GetString(preset, "source", "");

                if (id.Length == 0 || !ids.Add(id)) bad++;
                if (url.Length == 0 || boot.Length == 0) bad++;
                if (kind != "fda" && kind != "hda" && kind != "cdrom") bad++;
                if (Json.GetInt(preset, "memory_mb", 0) <= 0) bad++;

                if (source == "copy.sh")
                {
                    copyCount++;
                }
                else if (source == "old-dos.ru")
                {
                    oldCount++;
                }
                else
                {
                    ownCount++;

                    // у своих хэш обязателен: это единственная защита от подмены
                    if (Json.GetString(preset, "sha256", "").Length != 64) ownHashed = false;
                }
            }

            Check(presets.Count > 100, "presets: catalog size (" + presets.Count + " entries)");
            Check(bad == 0, "presets: ids and fields (" + bad + " invalid)");
            Check(ownCount >= 5 && copyCount > 100,
                "presets: catalogs (" + ownCount + " own + " + copyCount + " copy.sh + " +
                oldCount + " old-dos)");
            Check(oldCount > 0, "presets: old-dos catalog is loaded (" + oldCount + ")");
            Check(ownHashed, "presets: own entries are hash-pinned");
        }

        /// <summary>
        /// Настройки хоста (hv86.json): шрифт текстового режима по умолчанию.
        /// Нужны, чтобы «шрифт как на хосте» не зависел от конкретной ВМ.
        /// </summary>
        private static void TestHostSettings()
        {
            HostSettings.Invalidate();

            HostSettings.TextFont = "Consolas";
            HostSettings.TextFontSize = 16;
            HostSettings.Invalidate();

            Check(HostSettings.TextFont == "Consolas" && HostSettings.TextFontSize == 16,
                "host settings: round-trip through hv86.json");

            Check(File.Exists(Path.Combine(Paths.Root, "hv86.json")),
                "host settings: file written");

            // границы: размер не может быть отрицательным или гигантским
            HostSettings.TextFontSize = -5;
            Check(HostSettings.TextFontSize == 0, "host settings: negative size is clamped");

            HostSettings.TextFontSize = 999;
            Check(HostSettings.TextFontSize == 48, "host settings: huge size is clamped");

            // способ выхода из захвата мыши: мусор приводится к дефолту
            HostSettings.CaptureRelease = "middle";
            Check(HostSettings.CaptureRelease == "middle", "host settings: capture release stored");

            HostSettings.CaptureRelease = "нет такого";
            Check(HostSettings.CaptureRelease == "f8f12", "host settings: bad capture mode falls back");

            HostSettings.TextFont = "";
            HostSettings.TextFontSize = 0;
        }

        /// <summary>
        /// Проверяем распознавание по сигнатуре и распаковку образа.
        /// </summary>
        private static void TestImagePack(string dir)
        {
            string pack = Path.Combine(dir, "pack");
            Directory.CreateDirectory(pack);

            // 1. ZIP с одним образом внутри (1.44M флоппи)
            string floppy = Path.Combine(pack, "disk.img");
            File.WriteAllBytes(floppy, new byte[1474560]);

            string zipPath = Path.Combine(pack, "dos.zip");

            using (System.IO.Compression.ZipArchive zip =
                   System.IO.Compression.ZipFile.Open(zipPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(floppy, "disk.img");

                using (StreamWriter writer = new StreamWriter(zip.CreateEntry("readme.txt").Open()))
                    writer.Write("hi");
            }

            Check(ImagePack.Detect(zipPath) == ImagePack.KindZip, "pack: zip detected");

            string unpacked = ImagePack.Unwrap(zipPath);
            Check(File.Exists(unpacked) && new FileInfo(unpacked).Length == 1474560,
                "pack: image extracted from zip");

            // 2. ZIP с двумя образами - непонятно, какой брать
            string twoPath = Path.Combine(pack, "two.zip");

            using (System.IO.Compression.ZipArchive zip =
                   System.IO.Compression.ZipFile.Open(twoPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(floppy, "a.img");
                zip.CreateEntryFromFile(floppy, "b.img");
            }

            bool refused = false;
            try { ImagePack.Unwrap(twoPath); }
            catch (Hv86Exception) { refused = true; }

            Check(refused, "pack: two images are refused");

            // 3. RAR: сигнатура есть, распаковщика нет - честный отказ
            string rarPath = Path.Combine(pack, "old.rar");
            byte[] rar = new byte[64];
            rar[0] = (byte)'R'; rar[1] = (byte)'a'; rar[2] = (byte)'r'; rar[3] = (byte)'!';
            File.WriteAllBytes(rarPath, rar);

            Check(ImagePack.Detect(rarPath) == ImagePack.KindRar, "pack: rar detected");

            refused = false;
            try { ImagePack.Unwrap(rarPath); }
            catch (Hv86Exception) { refused = true; }

            Check(refused, "pack: rar is refused with a message");

            // 4. ISO: сигнатура CD001 по смещению 0x8001
            string isoPath = Path.Combine(pack, "cd.iso");
            byte[] iso = new byte[0x8000 + 16];
            iso[0x8001] = (byte)'C'; iso[0x8002] = (byte)'D'; iso[0x8003] = (byte)'0';
            iso[0x8004] = (byte)'0'; iso[0x8005] = (byte)'1';
            File.WriteAllBytes(isoPath, iso);

            Check(ImagePack.Detect(isoPath) == ImagePack.KindIso, "pack: iso detected");
            Check(ImagePack.Unwrap(isoPath) == isoPath, "pack: iso is used as is");

            // 5. Обычный образ - как есть
            Check(ImagePack.Detect(floppy) == ImagePack.KindRaw &&
                  ImagePack.Unwrap(floppy) == floppy, "pack: raw image is used as is");

            // 6. Набор дискет: все образы по порядку, а не "какой брать"
            List<string> set = ImagePack.UnwrapSet(twoPath);

            Check(set.Count == 2 && File.Exists(set[0]) && File.Exists(set[1]),
                "pack: set of 2 floppies is unwrapped");

            Check(set[0].EndsWith("two-a.img", StringComparison.OrdinalIgnoreCase) &&
                  set[1].EndsWith("two-b.img", StringComparison.OrdinalIgnoreCase),
                "pack: set keeps archive order");

            // 7. Порядок дискет естественный: disk2 раньше disk10
            string manyPath = Path.Combine(pack, "many.zip");

            using (System.IO.Compression.ZipArchive zip =
                   System.IO.Compression.ZipFile.Open(manyPath, System.IO.Compression.ZipArchiveMode.Create))
            {
                zip.CreateEntryFromFile(floppy, "disk10.img");
                zip.CreateEntryFromFile(floppy, "disk2.img");
                zip.CreateEntryFromFile(floppy, "disk1.img");
            }

            List<string> many = ImagePack.UnwrapSet(manyPath);

            Check(many.Count == 3 &&
                  many[0].EndsWith("disk1.img", StringComparison.OrdinalIgnoreCase) &&
                  many[1].EndsWith("disk2.img", StringComparison.OrdinalIgnoreCase) &&
                  many[2].EndsWith("disk10.img", StringComparison.OrdinalIgnoreCase),
                "pack: set is sorted naturally (disk1, disk2, disk10)");

            // 8. Комплект переживает запись и чтение vm.json
            VmDefinition vm = new VmDefinition();
            vm.Id = "settest";
            vm.HdaFile = null;
            vm.FdaFile = many[0];
            vm.FdaSet = many;
            vm.FdaIndex = 1;

            VmDefinition back = VmDefinition.FromDict(vm.ToDict());

            Check(back.FdaSet.Count == 3 && back.FdaIndex == 1 &&
                  back.FdaFile == many[1],
                "pack: floppy set survives vm.json round-trip");
            Check(Json.GetInt(back.ToView(), "floppy_count", 0) == 3 &&
                  Json.GetInt(back.ToView(), "floppy_index", -1) == 1,
                "pack: view reports floppy set size and position");
        }

        private static void TestShare(string dir)
        {
            string share = Path.Combine(dir, "share");
            Directory.CreateDirectory(share);
            File.WriteAllText(Path.Combine(share, "a.txt"), "hello", new UTF8Encoding(false));

            ShareFileServer server = new ShareFileServer(share, false);

            Dictionary<string, object> stat = server.Execute(Json.Obj("op", "stat", "path", "a.txt"));
            Check(!Json.GetBool(stat, "dir", true) && Json.GetLong(stat, "size", 0) == 5, "share: stat file");

            List<object> entries = Json.GetList(server.Execute(Json.Obj("op", "readdir", "path", "")), "entries");
            Check(entries != null && entries.Count >= 1, "share: readdir");

            Dictionary<string, object> read = server.Execute(
                Json.Obj("op", "read", "path", "a.txt", "offset", 0L, "count", 16));

            string data = Encoding.UTF8.GetString(Convert.FromBase64String(Json.GetString(read, "data", "")));
            Check(data == "hello", "share: read");

            Dictionary<string, object> write = server.Execute(Json.Obj(
                "op", "write", "path", "a.txt", "offset", 0L,
                "data", Convert.ToBase64String(Encoding.UTF8.GetBytes("hi"))));

            Check(Json.GetInt(write, "written", 0) == 2 &&
                  File.ReadAllText(Path.Combine(share, "a.txt"), Encoding.UTF8) == "hillo",
                "share: write at offset");

            Check(server.Execute(Json.Obj("op", "mkdir", "path", "sub")).ContainsKey("ok"), "share: mkdir");
            Check(server.Execute(Json.Obj("op", "rename", "from", "a.txt", "to", "b.txt")).ContainsKey("ok"),
                "share: rename");
            Check(File.Exists(Path.Combine(share, "b.txt")), "share: rename landed");

            Check(Json.GetString(server.Execute(Json.Obj("op", "stat", "path", "../outside")), "error", "") == "EACCES",
                "share: .. is refused");
            Check(Json.GetString(server.Execute(Json.Obj("op", "stat", "path", "..\\outside")), "error", "") == "EACCES",
                "share: backslash traversal is refused");
            Check(Json.GetString(server.Execute(Json.Obj("op", "stat", "path", "C:\\Windows")), "error", "") == "EACCES",
                "share: absolute path is refused");

            ShareFileServer readOnly = new ShareFileServer(share, true);

            Check(Json.GetString(readOnly.Execute(Json.Obj(
                      "op", "write", "path", "b.txt", "offset", 0L,
                      "data", Convert.ToBase64String(Encoding.UTF8.GetBytes("x")))), "error", "") == "EROFS",
                "share: read-only returns EROFS");
        }

        private static void Check(bool ok, string what)
        {
            if (ok) Console.WriteLine("  [ok]   " + what);
            else Fail(what);
        }

        private static void Fail(string text)
        {
            _failures++;
            Console.WriteLine("  [FAIL] " + text);
        }
    }
}