using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>
    /// Экспорт/импорт ВМ одним архивом `.hv86x` (обычный zip).
    ///
    /// Внутри:
    ///   vm.json              описание (id переписывается при импорте)
    ///   disks/…  media/…     образы
    ///   snapshots/…          только если явно попросили (они тяжёлые:
    ///                        каждый снапшот содержит копию диска)
    ///
    /// Безопасность импорта: архив — это чужой ввод. Поэтому никаких путей
    /// с `..` и абсолютных путей, никакой распаковки «как получится»:
    /// сначала проверяем каждую запись, потом распаковываем во временный
    /// каталог, и только потом переносим в vms\&lt;id&gt;.
    /// </summary>
    public static class VmArchive
    {
        public const string Extension = ".hv86x";

        // ---------------------------------------------------------------- экспорт

        public static Dictionary<string, object> Export(VmDefinition vm, string targetPath, bool includeSnapshots)
        {
            if (vm == null) throw new Hv86Exception("ВМ не найдена");
            if (string.IsNullOrEmpty(targetPath)) throw new Hv86Exception("не указан файл экспорта");

            string source = Paths.VmDir(vm.Id);
            if (!Directory.Exists(source)) throw new Hv86Exception("каталог ВМ не найден");

            if (!targetPath.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
                targetPath += Extension;

            string directory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            if (File.Exists(targetPath)) File.Delete(targetPath);

            int files = 0;
            long bytes = 0;

            using (FileStream stream = new FileStream(targetPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
                {
                    string relative = Path.GetRelativePath(source, file).Replace('\\', '/');

                    if (!includeSnapshots && relative.StartsWith("snapshots/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (relative.StartsWith("temp/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (relative.StartsWith("checkpoints/", StringComparison.OrdinalIgnoreCase)) continue;
                    if (relative.EndsWith(".part", StringComparison.OrdinalIgnoreCase)) continue;

                    ZipArchiveEntry entry = archive.CreateEntry(relative, CompressionLevel.Fastest);

                    using (Stream target = entry.Open())
                    using (FileStream input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        input.CopyTo(target);

                    files++;
                    bytes += new FileInfo(file).Length;
                }

                // версия формата рядом с данными: старые сборки смогут отказаться
                ZipArchiveEntry meta = archive.CreateEntry("hv86-archive.json", CompressionLevel.NoCompression);
                using (StreamWriter writer = new StreamWriter(meta.Open()))
                    writer.Write(Json.Pretty(Json.Obj(
                        "format", 1,
                        "product", AppInfo.ProductName,
                        "version", AppInfo.Version,
                        "v86", AppInfo.V86Version,
                        "exported_utc", DateTime.UtcNow.ToString("o"),
                        "include_snapshots", includeSnapshots)));
            }

            Log.Info("VM", "экспорт " + vm.Id + " -> " + targetPath + " (" + files + " файлов)");

            return Json.Obj(
                "path", targetPath,
                "size", new FileInfo(targetPath).Length,
                "files", files,
                "source_bytes", bytes);
        }

        // ---------------------------------------------------------------- импорт

        public static VmDefinition Import(string archivePath, out string newId)
        {
            newId = null;

            if (string.IsNullOrEmpty(archivePath) || !File.Exists(archivePath))
                throw new Hv86Exception("архив не найден: " + archivePath);

            string staging = Path.Combine(Paths.Root, "import-" + Guid.NewGuid().ToString("N").Substring(0, 8));

            try
            {
                Directory.CreateDirectory(staging);
                ExtractSafely(archivePath, staging);

                string vmFile = FindVmFile(staging);
                if (vmFile == null) throw new Hv86Exception("в архиве нет vm.json — это не ВМ hv86");

                Dictionary<string, object> data = Json.ReadFile(vmFile);
                if (data == null) throw new Hv86Exception("vm.json в архиве повреждён");

                VmDefinition vm = VmDefinition.FromDict(data);
                if (vm == null) throw new Hv86Exception("не удалось разобрать vm.json");

                newId = VmStore.NewId();

                vm.Id = newId;
                vm.State = "stopped";
                vm.Restarts = 0;
                vm.LastError = "";
                vm.CreatedUtc = DateTime.UtcNow.ToString("o");

                if (string.IsNullOrEmpty(vm.Name)) vm.Name = "imported-" + newId;

                string vmRoot = Path.GetDirectoryName(vmFile);
                string target = Paths.VmDir(newId);

                if (Directory.Exists(target)) throw new Hv86Exception("id уже занят: " + newId);

                Directory.CreateDirectory(target);

                // переносим содержимое каталога с vm.json (обычно это корень архива)
                foreach (string dir in Directory.GetDirectories(vmRoot))
                {
                    string name = Path.GetFileName(dir);
                    if (name.Equals("snapshots", StringComparison.OrdinalIgnoreCase)) continue;

                    AtomicFile.CopyDirectory(dir, Path.Combine(target, name));
                }

                foreach (string file in Directory.GetFiles(vmRoot))
                {
                    string name = Path.GetFileName(file);
                    if (name.Equals("vm.json", StringComparison.OrdinalIgnoreCase)) continue;
                    if (name.Equals("hv86-archive.json", StringComparison.OrdinalIgnoreCase)) continue;

                    File.Copy(file, Path.Combine(target, name), true);
                }

                VmStore.Save(vm);

                Log.Info("VM", "импорт " + archivePath + " -> " + newId);
                return vm;
            }
            finally
            {
                try { if (Directory.Exists(staging)) Directory.Delete(staging, true); } catch { }
            }
        }

        // ------------------------------------------------------------- утилиты

        private static void ExtractSafely(string archivePath, string targetDir)
        {
            string root = Path.GetFullPath(targetDir);

            using (ZipArchive archive = ZipFile.OpenRead(archivePath))
            {
                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue;    // каталог

                    string name = entry.FullName.Replace('\\', '/');

                    if (name.StartsWith("/", StringComparison.Ordinal) ||
                        name.IndexOf(":", StringComparison.Ordinal) >= 0)
                        throw new Hv86Exception("архив содержит недопустимый путь: " + name);

                    string full = Path.GetFullPath(Path.Combine(root, name));

                    if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                        throw new Hv86Exception("архив пытается выйти за пределы каталога: " + name);

                    string directory = Path.GetDirectoryName(full);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    entry.ExtractToFile(full, true);
                }
            }
        }

        private static string FindVmFile(string root)
        {
            string direct = Path.Combine(root, "vm.json");
            if (File.Exists(direct)) return direct;

            foreach (string file in Directory.GetFiles(root, "vm.json", SearchOption.AllDirectories))
                return file;

            return null;
        }
    }
}