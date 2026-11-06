using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>
    /// Каталог готовых образов (assets\presets.json, вшит в exe).
    ///
    /// Образ скачивается один раз в %ProgramData%\hv86\cache\presets и
    /// проверяется по SHA-256. Без совпадения хэша образ не используется -
    /// иначе «быстрый старт» однажды скачает что-нибудь совсем другое.
    /// </summary>
    public static class PresetCatalog
    {
        public static List<Dictionary<string, object>> All()
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();

            try
            {
                string text = Resources.Text(Resources.Presets);
                Dictionary<string, object> root = Json.ParseObjectSafe(text);
                List<object> presets = Json.GetList(root, "presets");

                if (presets != null)
                    foreach (object item in presets)
                    {
                        Dictionary<string, object> preset = item as Dictionary<string, object>;
                        if (preset != null) result.Add(preset);
                    }
            }
            catch (Exception ex)
            {
                Log.Exception("PRESET", "каталог пресетов", ex);
            }

            return result;
        }

        public static Dictionary<string, object> Get(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;

            foreach (Dictionary<string, object> preset in All())
                if (string.Equals(Json.GetString(preset, "id", ""), id, StringComparison.OrdinalIgnoreCase))
                    return preset;

            return null;
        }

        /// <summary>
        /// Возвращает локальный путь к образу пресета, скачивая его при
        /// необходимости. Бросает Hv86Exception, если скачать/проверить нельзя.
        /// </summary>
        public static string EnsureLocalFile(Dictionary<string, object> preset)
        {
            if (preset == null) throw new Hv86Exception("пресет не найден");

            string id = Json.GetString(preset, "id", "");
            string url = Json.GetString(preset, "url", "");
            string expected = Json.GetString(preset, "sha256", "");
            long size = Json.GetLong(preset, "size", 0);

            if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(url))
                throw new Hv86Exception("у пресета нет id или url");

            Paths.EnsureDirectories();

            string extension = Path.GetExtension(new Uri(url).AbsolutePath);
            if (string.IsNullOrEmpty(extension)) extension = ".img";

            string fileName = id + "-" + (expected.Length >= 8 ? expected.Substring(0, 8) : "nohash") + extension;
            string path = Path.Combine(Paths.PresetCacheDir, fileName);

            if (File.Exists(path))
            {
                if (string.IsNullOrEmpty(expected) || Matches(path, expected)) return path;

                Log.Warn("PRESET", "хэш кэша не совпал, качаю заново: " + fileName);
                try { File.Delete(path); } catch { }
            }

            string temp = path + ".part";

            try
            {
                Log.Info("PRESET", "скачивание " + url);

                using (WebClient client = new WebClient())
                {
                    client.Headers.Add("User-Agent", "hv86/" + AppInfo.Version);
                    client.DownloadFile(url, temp);
                }

                long actual = new FileInfo(temp).Length;
                if (size > 0 && actual != size)
                    Log.Warn("PRESET", "размер не совпал: ожидали " + size + ", получили " + actual);

                if (!string.IsNullOrEmpty(expected))
                {
                    string hash = AtomicFile.Sha256(temp);
                    if (!string.Equals(hash, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        File.Delete(temp);
                        throw new Hv86Exception("хэш образа не совпал: образ не будет использован");
                    }
                }

                AtomicFile.Move(temp, path);
                Log.Info("PRESET", "образ готов: " + path);
                return path;
            }
            catch (Hv86Exception)
            {
                throw;
            }
            catch (Exception ex)
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                throw new Hv86Exception("не удалось скачать образ: " + ex.Message);
            }
        }

        private static bool Matches(string path, string expectedHash)
        {
            string actual = AtomicFile.Sha256(path);
            return string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
    }
}