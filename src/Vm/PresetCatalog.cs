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
    ///
    /// Второй каталог (assets\presets-copy.json) - образы демо-стенда
    /// copy.sh/v86: там 100+ систем, но у них нет хэшей. Для них работает
    /// закрепление при первой загрузке: скачали, посчитали, записали рядом
    /// `.sha256` и дальше сверяем - смена файла на сервере не пройдёт молча.
    /// </summary>
    public static class PresetCatalog
    {
        public static List<Dictionary<string, object>> All()
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();

            Append(result, Resources.Presets, null);
            Append(result, Resources.PresetsCopy, "copy.sh");
            Append(result, Resources.PresetsOldDos, "old-dos.ru");

            return result;
        }

        private static void Append(List<Dictionary<string, object>> result, string resource, string source)
        {
            try
            {
                string text = Resources.Text(resource);
                if (string.IsNullOrEmpty(text)) return;

                Dictionary<string, object> root = Json.ParseObjectSafe(text);
                List<object> presets = Json.GetList(root, "presets");
                if (presets == null) return;

                foreach (object item in presets)
                {
                    Dictionary<string, object> preset = item as Dictionary<string, object>;
                    if (preset == null) continue;

                    // одинаковые id не должны перетирать друг друга: второй
                    // каталог и так идёт с префиксом v86-, но проверим
                    string id = Json.GetString(preset, "id", "");
                    if (id.Length == 0) continue;

                    bool exists = false;
                    foreach (Dictionary<string, object> known in result)
                        if (string.Equals(Json.GetString(known, "id", ""), id, StringComparison.OrdinalIgnoreCase))
                        {
                            exists = true;
                            break;
                        }

                    if (exists)
                    {
                        Log.Warn("PRESET", "дубль id в каталоге: " + id);
                        continue;
                    }

                    if (source != null) preset["source"] = source;

                    result.Add(preset);
                }
            }
            catch (Exception ex)
            {
                Log.Exception("PRESET", "каталог " + resource, ex);
            }
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
                if (!string.IsNullOrEmpty(expected))
                {
                    if (Matches(path, expected)) return ImagePack.Unwrap(path);

                    Log.Warn("PRESET", "хэш кэша не совпал, качаю заново: " + fileName);
                    try { File.Delete(path); } catch { }
                }
                else
                {
                    string pinned = ReadPinned(path);

                    // хэш закрепили при первой загрузке - дальше сверяем его
                    if (pinned.Length == 0 || Matches(path, pinned)) return ImagePack.Unwrap(path);

                    Log.Warn("PRESET", "образ изменился с первой загрузки, качаю заново: " + fileName);
                    try { File.Delete(path); } catch { }
                }
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
                else
                {
                    PinHash(temp, path);
                }

                AtomicFile.Move(temp, path);
                Log.Info("PRESET", "образ готов: " + path);

                // старые каталоги отдают ZIP с образом внутри: распаковываем,
                // иначе ВМ получит архив вместо диска и не загрузится
                return ImagePack.Unwrap(path);
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

        /// <summary>
        /// Закрепляем хэш при первой загрузке. Нужно для каталога copy.sh: у его
        /// образов хэша нет, а молча принимать любые байты с сервера - плохо.
        /// Со второй загрузки сверяем сохранённый `.sha256`.
        /// </summary>
        private static void PinHash(string downloadedFile, string targetPath)
        {
            try
            {
                string hash = AtomicFile.Sha256(downloadedFile);

                File.WriteAllText(targetPath + ".sha256", hash + Environment.NewLine,
                    new System.Text.UTF8Encoding(false));

                Log.Info("PRESET", "хэш закреплён при первой загрузке: " + hash);
            }
            catch (Exception ex)
            {
                Log.Warn("PRESET", "не удалось закрепить хэш: " + ex.Message);
            }
        }

        private static string ReadPinned(string path)
        {
            try
            {
                string sidecar = path + ".sha256";
                return File.Exists(sidecar) ? File.ReadAllText(sidecar).Trim() : "";
            }
            catch
            {
                return "";
            }
        }
    }
}