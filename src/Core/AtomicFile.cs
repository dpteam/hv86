using System;
using System.IO;
using System.Text;

namespace Hv86.Core
{
    /// <summary>
    /// Атомарная запись файлов: пишем во временный файл рядом, потом
    /// подменяем. Нужно потому, что vm.json/settings.json читает и служба,
    /// и процессы ВМ: они не должны увидеть файл наполовину записанным.
    /// </summary>
    public static class AtomicFile
    {
        public static void WriteAllText(string path, string text)
        {
            WriteAllBytes(path, new UTF8Encoding(false).GetBytes(text ?? string.Empty));
        }

        public static void WriteAllBytes(string path, byte[] bytes)
        {
            if (string.IsNullOrEmpty(path)) throw new ArgumentNullException("path");
            byte[] data = bytes ?? new byte[0];

            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            string temp = path + "." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".tmp";

            try
            {
                using (FileStream stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    stream.Write(data, 0, data.Length);
                    stream.Flush(true);
                }

                if (File.Exists(path))
                {
                    // File.Move с overwrite на NTFS - атомарная подмена
                    File.Move(temp, path, true);
                }
                else
                {
                    File.Move(temp, path);
                }
            }
            finally
            {
                try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            }
        }

        /// <summary>Перемещение файла с созданием каталогов.</summary>
        public static void Move(string from, string to)
        {
            string dir = Path.GetDirectoryName(to);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
            if (File.Exists(to)) File.Delete(to);
            File.Move(from, to);
        }

        /// <summary>Создание разреженного файла нужного размера (образ диска).</summary>
        public static void CreateSparse(string path, long size)
        {
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

            using (FileStream stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                stream.SetLength(size);
                stream.Flush(true);
            }
        }

        /// <summary>Удаление в корзину hv86: безвозвратного удаления в проекте нет.</summary>
        public static void MoveToTrash(string path, string hint)
        {
            if (string.IsNullOrEmpty(path)) return;
            if (!File.Exists(path) && !Directory.Exists(path)) return;

            Paths.EnsureDirectories();
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            string safeHint = string.IsNullOrEmpty(hint) ? "item" : hint;
            foreach (char bad in Path.GetInvalidFileNameChars())
                safeHint = safeHint.Replace(bad, '_');

            string target = Path.Combine(Paths.TrashDir, stamp + "-" + safeHint);
            int n = 1;
            while (File.Exists(target) || Directory.Exists(target))
                target = Path.Combine(Paths.TrashDir, stamp + "-" + safeHint + "-" + (n++));

            if (Directory.Exists(path)) MoveDirectory(path, target);
            else File.Move(path, target);
        }

        /// <summary>
        /// Перенос каталога в корзину. `Directory.Move` умеет только в пределах
        /// тома, а корзина и данные вполне могут оказаться на разных дисках
        /// (в CI так и было: %TEMP% на C:, RUNNER_TEMP на D:). Тогда копируем
        /// и удаляем источник - данные уже лежат в корзине, терять нечего.
        /// </summary>
        private static void MoveDirectory(string from, string to)
        {
            try
            {
                Directory.Move(from, to);
            }
            catch (IOException)
            {
                CopyDirectory(from, to);
                Directory.Delete(from, true);

                Log.Info("TRASH", "каталог перенесён между томами: " + to);
            }
        }

        public static void CopyDirectory(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(dir.Replace(from, to));

            foreach (string file in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                File.Copy(file, file.Replace(from, to), true);
        }

        public static string Sha256(string path)
        {
            try
            {
                using (FileStream stream = File.OpenRead(path))
                using (System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create())
                {
                    byte[] hash = sha.ComputeHash(stream);
                    StringBuilder sb = new StringBuilder(hash.Length * 2);
                    foreach (byte b in hash) sb.Append(b.ToString("x2"));
                    return sb.ToString();
                }
            }
            catch
            {
                return null;
            }
        }
    }
}