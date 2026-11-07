using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Hv86.Core;
using Microsoft.Win32;

namespace Hv86.Core
{
    /// <summary>
    /// Шрифты для текстового режима: системные и загруженные пользователем.
    ///
    /// Свои шрифты лежат файлами в %HV86_ROOT%\fonts и перечислены в hv86.json.
    /// Имя семейства берём из таблицы `name` самого файла (nameID 1/16), а не из
    /// имени файла: пользователь качает «consola.ttf», а семейство там «Consolas»,
    /// и в CSS нужен именно второй вариант.
    ///
    /// Ошибки разбора не фатальны: если имя не достали, берём имя файла - шрифт
    /// всё равно применится, просто под своим «файловым» именем.
    /// </summary>
    public static class FontCatalog
    {
        public static string Dir
        {
            get { return Path.Combine(Paths.Root, "fonts"); }
        }

        /// <summary>Системные семейства (без повторов), по алфавиту.</summary>
        public static List<string> SystemFonts()
        {
            SortedSet<string> names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                using (RegistryKey key = Registry.LocalMachine.OpenSubKey(
                           @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Fonts"))
                {
                    if (key != null)
                    {
                        foreach (string value in key.GetValueNames())
                        {
                            string family = CleanFamily(value);
                            if (family.Length > 0) names.Add(family);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("FONT", "реестр шрифтов: " + ex.Message);
            }

            // запасной путь: имена файлов в каталоге шрифтов (на случай
            // урезанного реестра). Семейство из файла не читаем - это дорого
            // для сотен файлов при каждом запросе списка.
            try
            {
                string dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);

                if (System.IO.Directory.Exists(dir) && names.Count == 0)
                {
                    foreach (string file in System.IO.Directory.GetFiles(dir, "*.ttf"))
                        names.Add(Path.GetFileNameWithoutExtension(file));
                }
            }
            catch { }

            return new List<string>(names);
        }

        /// <summary>Загруженные пользователем шрифты: [{family, file}].</summary>
        public static List<Dictionary<string, object>> Custom()
        {
            List<Dictionary<string, object>> result = new List<Dictionary<string, object>>();

            foreach (Dictionary<string, object> item in HostSettings.Fonts())
            {
                string file = Json.GetString(item, "file", "");
                string family = Json.GetString(item, "family", "");

                if (file.Length == 0 || family.Length == 0) continue;

                // файл могли удалить руками - запись без файла не показываем
                if (!File.Exists(Path.Combine(Dir, file))) continue;

                result.Add(Json.Obj("family", family, "file", file));
            }

            return result;
        }

        /// <summary>Всё, что можно выбрать: системные + свои.</summary>
        public static List<string> All()
        {
            SortedSet<string> names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string name in SystemFonts()) names.Add(name);
            foreach (Dictionary<string, object> item in Custom()) names.Add(Json.GetString(item, "family", ""));

            return new List<string>(names);
        }

        /// <summary>
        /// Копирует шрифт в каталог hv86 и запоминает его. Возвращает запись.
        /// </summary>
        public static Dictionary<string, object> Import(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path))
                throw new Hv86Exception("файл шрифта не найден: " + path);

            string ext = Path.GetExtension(path).ToLowerInvariant();

            if (ext != ".ttf" && ext != ".otf" && ext != ".ttc")
                throw new Hv86Exception("поддерживаются .ttf, .otf и .ttc, а не " + ext);

            System.IO.Directory.CreateDirectory(Dir);

            string family = FamilyName(path);
            string baseName = SafeName(family);
            string target = Path.Combine(Dir, baseName + ext);

            // файл с тем же именем перезаписываем: это обновление шрифта
            File.Copy(path, target, true);

            List<Dictionary<string, object>> list = HostSettings.Fonts();
            List<Dictionary<string, object>> updated = new List<Dictionary<string, object>>();

            foreach (Dictionary<string, object> item in list)
                if (!string.Equals(Json.GetString(item, "file", ""), baseName + ext, StringComparison.OrdinalIgnoreCase))
                    updated.Add(item);

            updated.Add(Json.Obj("family", family, "file", baseName + ext));
            HostSettings.Fonts(updated);

            Log.Info("FONT", "загружен шрифт " + family + " (" + baseName + ext + ")");

            return Json.Obj("family", family, "file", baseName + ext);
        }

        public static void Remove(string file)
        {
            if (string.IsNullOrEmpty(file)) return;

            string name = Path.GetFileName(file);
            if (name != file) throw new Hv86Exception("некорректное имя файла шрифта");

            List<Dictionary<string, object>> updated = new List<Dictionary<string, object>>();

            foreach (Dictionary<string, object> item in HostSettings.Fonts())
                if (!string.Equals(Json.GetString(item, "file", ""), name, StringComparison.OrdinalIgnoreCase))
                    updated.Add(item);

            HostSettings.Fonts(updated);

            try
            {
                string path = Path.Combine(Dir, name);
                if (File.Exists(path)) AtomicFile.MoveToTrash(path, "font-" + name);
            }
            catch (Exception ex)
            {
                Log.Warn("FONT", "удаление шрифта " + name + ": " + ex.Message);
            }
        }

        /// <summary>Полный путь к файлу своего шрифта или null.</summary>
        public static string FilePath(string file)
        {
            if (string.IsNullOrEmpty(file)) return null;

            string name = Path.GetFileName(file);
            if (name != file) return null;

            string path = Path.Combine(Dir, name);

            return File.Exists(path) ? path : null;
        }

        // ------------------------------------------------------------ имя семейства

        /// <summary>
        /// Имя семейства из таблицы `name` (nameID 1, для TTC - из первого шрифта).
        /// Не разобралось - имя файла без расширения.
        /// </summary>
        public static string FamilyName(string path)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    string name = ReadFamily(stream);
                    if (!string.IsNullOrEmpty(name)) return name.Trim();
                }
            }
            catch (Exception ex)
            {
                Log.Warn("FONT", "имя семейства " + Path.GetFileName(path) + ": " + ex.Message);
            }

            return Path.GetFileNameWithoutExtension(path);
        }

        private static string ReadFamily(Stream stream)
        {
            byte[] head = new byte[12];
            if (stream.Read(head, 0, 12) != 12) return null;

            uint tag = BigEndian(head, 0);

            // 'ttcf' - коллекция: берём первый шрифт внутри
            if (tag == 0x74746366)
            {
                byte[] count = new byte[4];
                if (stream.Read(count, 0, 4) != 4) return null;

                byte[] offset = new byte[4];
                if (stream.Read(offset, 0, 4) != 4) return null;

                long first = BigEndian(offset, 0);
                stream.Seek(first, SeekOrigin.Begin);

                if (stream.Read(head, 0, 12) != 12) return null;
                tag = BigEndian(head, 0);
            }

            // 0x00010000 (TrueType), 'OTTO' (CFF), 'true'/'typ1'
            if (tag != 0x00010000 && tag != 0x4F54544F && tag != 0x74727565 && tag != 0x74797031)
                return null;

            int tables = (head[4] << 8) | head[5];

            for (int i = 0; i < tables; i++)
            {
                byte[] record = new byte[16];
                if (stream.Read(record, 0, 16) != 16) return null;

                if (BigEndian(record, 0) != 0x6E616D65) continue; // 'name'

                long offset = BigEndian(record, 8);
                long length = BigEndian(record, 12);

                return ReadNameTable(stream, offset, length);
            }

            return null;
        }

        private static string ReadNameTable(Stream stream, long offset, long length)
        {
            if (length <= 0 || length > 1024 * 1024) return null;

            byte[] table = new byte[length];
            stream.Seek(offset, SeekOrigin.Begin);

            int read = 0;
            while (read < length)
            {
                int chunk = stream.Read(table, read, (int)length - read);
                if (chunk <= 0) break;
                read += chunk;
            }

            if (read < 6) return null;

            int count = (table[2] << 8) | table[3];
            int stringsOffset = (table[4] << 8) | table[5];

            string best = null;
            int bestScore = -1;

            for (int i = 0; i < count; i++)
            {
                int at = 6 + i * 12;
                if (at + 12 > read) break;

                int platform = (table[at] << 8) | table[at + 1];
                int encoding = (table[at + 2] << 8) | table[at + 3];
                int nameId = (table[at + 6] << 8) | table[at + 7];
                int size = (table[at + 8] << 8) | table[at + 9];
                int nameOffset = (table[at + 10] << 8) | table[at + 11];

                // 1 - семейство, 16 - типографское семейство (предпочтительнее)
                if (nameId != 1 && nameId != 16) continue;

                int start = stringsOffset + nameOffset;
                if (start < 0 || start + size > read || size <= 0) continue;

                string text;

                if (platform == 3)
                {
                    // Windows: UTF-16BE
                    StringBuilder builder = new StringBuilder(size / 2);

                    for (int b = start; b + 1 < start + size; b += 2)
                        builder.Append((char)((table[b] << 8) | table[b + 1]));

                    text = builder.ToString();
                }
                else if (platform == 1)
                {
                    // Mac Roman: для латиницы совпадает с ASCII
                    text = Encoding.ASCII.GetString(table, start, size);
                }
                else continue;

                text = text.Trim();
                if (text.Length == 0) continue;

                int score = (nameId == 16 ? 2 : 1) + (platform == 3 ? 2 : 0);

                if (score > bestScore)
                {
                    best = text;
                    bestScore = score;
                }
            }

            return best;
        }

        private static uint BigEndian(byte[] data, int at)
        {
            return (uint)((data[at] << 24) | (data[at + 1] << 16) | (data[at + 2] << 8) | data[at + 3]);
        }

        private static string CleanFamily(string value)
        {
            string text = value ?? "";

            int bracket = text.IndexOf('(');
            if (bracket > 0) text = text.Substring(0, bracket);

            text = text.Trim();

            // «Arial Bold Italic» - это начертание, а не семейство
            foreach (string suffix in new[] { " Bold Italic", " Bold", " Italic", " Light", " Regular", " Oblique" })
                if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    text = text.Substring(0, text.Length - suffix.Length).Trim();

            return text;
        }

        private static string SafeName(string family)
        {
            StringBuilder builder = new StringBuilder(family.Length);

            foreach (char c in family)
                builder.Append(Path.GetInvalidFileNameChars().IndexOf(c) >= 0 ? '_' : c);

            string text = builder.ToString().Trim();

            return text.Length == 0 ? "font" : text;
        }
    }
}