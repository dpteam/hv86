using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using Hv86.Core;
using Hv86.Vm;

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