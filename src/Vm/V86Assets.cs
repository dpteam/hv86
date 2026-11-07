using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>
    /// Распаковка вшитых ассетов v86 для headless-движка.
    ///
    /// WebEngine берёт BIOS и wasm прямо из ресурсов exe через свой
    /// ресурсный сервер. Node так не умеет: ему нужны настоящие файлы.
    /// Поэтому при первом headless-запуске выкладываем их в кэш
    /// (%ProgramData%\hv86\cache\v86\&lt;версия&gt;\) и дальше только проверяем размер.
    /// </summary>
    public static class V86Assets
    {
        private static readonly object Gate = new object();
        private static string _dir;

        public static string Directory
        {
            get
            {
                lock (Gate)
                {
                    if (_dir != null) return _dir;

                    _dir = Path.Combine(Paths.CacheDir, "v86", AppInfo.V86Version);
                    return _dir;
                }
            }
        }

        /// <summary>
        /// Гарантирует наличие seabios.bin, vgabios.bin и v86.wasm.
        /// Возвращает каталог или null с текстом ошибки.
        /// </summary>
        public static string Ensure(out string error)
        {
            error = null;

            try
            {
                lock (Gate)
                {
                    string dir = Directory;
                    System.IO.Directory.CreateDirectory(dir);

                    Extract(dir, "seabios.bin", Resources.V86Seabios);
                    Extract(dir, "vgabios.bin", Resources.V86Vgabios);
                    Extract(dir, "v86.wasm", Resources.V86Wasm);

                    // без wasm v86 в Node не стартует - проверяем, что он на месте
                    string wasm = Path.Combine(dir, "v86.wasm");
                    if (!File.Exists(wasm) || new FileInfo(wasm).Length < 1024 * 1024)
                    {
                        error = "v86.wasm не распаковался";
                        return null;
                    }

                    return dir;
                }
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return null;
            }
        }

        /// <summary>
        /// Сверяет вшитые ассеты v86 с манифестом: версия, размер и SHA-256.
        /// Ловит самый неприятный случай - файлы заменили, а версию и хэши
        /// забыли обновить: тогда ВМ создана на одной версии, а исполняется
        /// другой, и state молча не подходит.
        /// </summary>
        public static List<Dictionary<string, object>> VerifyEmbedded()
        {
            List<Dictionary<string, object>> results = new List<Dictionary<string, object>>();

            Dictionary<string, object> manifest = Json.ParseObjectSafe(Resources.Text(Resources.V86Manifest));
            string version = Json.GetString(manifest, "version", "");

            if (version.Length == 0)
            {
                results.Add(Json.Obj(
                    "what", "manifest",
                    "ok", false,
                    "detail", "манифест v86 не вшит или пуст"));
                return results;
            }

            results.Add(Json.Obj(
                "what", "v86 version",
                "ok", version == AppInfo.V86Version,
                "detail", "манифест " + version + ", приложение " + AppInfo.V86Version));

            List<object> files = Json.GetList(manifest, "files");

            if (files == null || files.Count == 0)
            {
                results.Add(Json.Obj("what", "manifest files", "ok", false, "detail", "список файлов пуст"));
                return results;
            }

            foreach (object item in files)
            {
                Dictionary<string, object> file = item as Dictionary<string, object>;
                if (file == null) continue;

                string name = Json.GetString(file, "file", "");
                long size = Json.GetLong(file, "size", 0);
                string sha = Json.GetString(file, "sha256", "");

                byte[] bytes = Resources.Bytes(ResourceFor(name));

                if (bytes == null)
                {
                    results.Add(Json.Obj("what", name, "ok", false, "detail", "нет вшитого ресурса"));
                    continue;
                }

                string actual = Sha256Of(bytes);
                bool ok = bytes.Length == size && string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase);

                results.Add(Json.Obj(
                    "what", name,
                    "ok", ok,
                    "detail", ok
                        ? bytes.Length + " байт, sha256 " + actual.Substring(0, 12) + "…"
                        : "ожидалось " + size + " байт / " + sha.Substring(0, Math.Min(12, sha.Length)) +
                          "…, в сборке " + bytes.Length + " байт / " + actual.Substring(0, 12) + "…"));
            }

            return results;
        }

        private static string ResourceFor(string fileName)
        {
            switch ((fileName ?? "").ToLowerInvariant())
            {
                case "libv86.js": return Resources.V86Lib;
                case "v86.wasm": return Resources.V86Wasm;
                case "seabios.bin": return Resources.V86Seabios;
                case "vgabios.bin": return Resources.V86Vgabios;
                default: return null;
            }
        }

        private static string Sha256Of(byte[] bytes)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(bytes);
                StringBuilder text = new StringBuilder(hash.Length * 2);

                foreach (byte b in hash) text.Append(b.ToString("x2"));

                return text.ToString();
            }
        }

        private static void Extract(string dir, string name, string resource)
        {
            string target = Path.Combine(dir, name);
            long expected = Resources.Size(resource);

            if (File.Exists(target) && new FileInfo(target).Length == expected) return;

            byte[] bytes = Resources.Bytes(resource);
            if (bytes == null) throw new Hv86Exception("нет вшитого ресурса " + resource);

            AtomicFile.WriteAllBytes(target, bytes);
            Log.Info("NODE", "распакован " + target + " (" + bytes.Length + " байт)");
        }
    }
}