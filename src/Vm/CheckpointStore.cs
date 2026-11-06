using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>
    /// Снапшоты ВМ и кольцо чекпоинтов.
    ///
    /// Состав снапшота:
    ///   state.bin - результат v86 save_state(): RAM, регистры, устройства и
    ///               «грязные» блоки дисков (v86 сериализует только изменённые
    ///               блоки, поэтому state заметно меньше размера диска);
    ///   disk.img  - полная копия системного диска на момент снапшота.
    ///
    /// Почему копия диска, а не дельта: v86 отдаёт наружу только state, а
    /// восстановление state опирается на текущее содержимое образа. Копия
    /// стоит размера диска, зато откат к любому снапшоту всегда корректен -
    /// даже если после него ВМ работала и писала на диск. Для дисков 256 МБ
    /// это приемлемая цена.
    ///
    /// Кольцо чекпоинтов (FT) устроено иначе: только state, без копии диска.
    /// Оно живёт до первого штатного сохранения диска и служит для подъёма
    /// ВМ после падения vmhost.
    ///
    /// Раскладка:
    ///   vms\&lt;id&gt;\snapshots\0001-имя\  meta.json + state.bin + disk.img
    ///   vms\&lt;id&gt;\checkpoints\ring-&lt;utc&gt;.bin (+ .json)
    /// </summary>
    public static class CheckpointStore
    {
        public const string KindSnapshot = "snapshot";
        public const string KindRing = "ring";

        // ------------------------------------------------------------- снапшоты

        public static Dictionary<string, object> Create(VmDefinition vm, string name, string stateTempFile,
            long instructions)
        {
            if (vm == null) throw new Hv86Exception("ВМ не найдена");
            if (!File.Exists(stateTempFile)) throw new Hv86Exception("state-файл не создан");

            string root = Paths.VmSnapshotsDir(vm.Id);
            Directory.CreateDirectory(root);

            int seq = NextSequence(root);
            string slug = Slug(name);
            string id = seq.ToString("D4", CultureInfo.InvariantCulture) + (slug.Length > 0 ? "-" + slug : "");
            string dir = Path.Combine(root, id);

            Directory.CreateDirectory(dir);

            long stateSize = new FileInfo(stateTempFile).Length;
            AtomicFile.Move(stateTempFile, Path.Combine(dir, "state.bin"));

            bool hasDiskCopy = false;

            try
            {
                string disk = vm.HdaPath;
                if (!string.IsNullOrEmpty(disk) && File.Exists(disk))
                {
                    File.Copy(disk, Path.Combine(dir, "disk.img"), true);
                    hasDiskCopy = true;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("SNAP", "копия диска", ex);
                throw new Hv86Exception("не удалось сохранить копию диска для снапшота: " + ex.Message);
            }

            Dictionary<string, object> meta = Json.Obj(
                "id", id,
                "name", string.IsNullOrEmpty(name) ? id : name,
                "kind", KindSnapshot,
                "created_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                "memory_mb", vm.MemoryMb,
                "v86_version", AppInfo.V86Version,
                "instructions", instructions,
                "size", stateSize,
                "disk_copy", hasDiskCopy,
                "disk_size", DiskSize(vm),
                "valid", true);

            Json.WriteFile(Path.Combine(dir, "meta.json"), meta);

            Log.Info("SNAP", "снапшот " + id + " для " + vm.Id + ": state " +
                             (stateSize / 1024 / 1024) + " МБ, диск " + (hasDiskCopy ? "скопирован" : "нет"));

            return View(meta, dir);
        }

        public static List<Dictionary<string, object>> List(string vmId)
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();

            try
            {
                string root = Paths.VmSnapshotsDir(vmId);
                if (!Directory.Exists(root)) return list;

                foreach (string dir in Directory.GetDirectories(root))
                {
                    Dictionary<string, object> meta = Json.ReadFile(Path.Combine(dir, "meta.json"));
                    if (meta == null) continue;

                    bool valid = File.Exists(Path.Combine(dir, "state.bin"));
                    if (!valid) continue;

                    meta["valid"] = true;
                    list.Add(View(meta, dir));
                }

                list.Sort((a, b) => string.Compare(Json.GetString(b, "id", ""), Json.GetString(a, "id", ""),
                    StringComparison.Ordinal));
            }
            catch (Exception ex)
            {
                Log.Exception("SNAP", "список снапшотов " + vmId, ex);
            }

            return list;
        }

        /// <summary>
        /// Готовит откат: возвращает путь к state.bin и восстанавливает образ
        /// диска из копии снапшота (если она есть). Вызывать при остановленной ВМ.
        /// </summary>
        public static string PrepareRevert(VmDefinition vm, string snapshotId)
        {
            if (vm == null) throw new Hv86Exception("ВМ не найдена");
            if (string.IsNullOrEmpty(snapshotId) || snapshotId.IndexOfAny(new[] { '\\', '/', ':' }) >= 0)
                throw new Hv86Exception("некорректный id снапшота");

            string dir = Path.Combine(Paths.VmSnapshotsDir(vm.Id), snapshotId);
            string metaFile = Path.Combine(dir, "meta.json");
            string state = Path.Combine(dir, "state.bin");

            if (!File.Exists(metaFile)) throw new Hv86Exception("снапшот не найден: " + snapshotId);
            if (!File.Exists(state)) throw new Hv86Exception("снапшот повреждён: нет state.bin");

            Dictionary<string, object> meta = Json.ReadFile(metaFile);

            string version = Json.GetString(meta, "v86_version", "");
            if (!string.IsNullOrEmpty(version) && version != AppInfo.V86Version)
                throw new Hv86Exception("снапшот сделан в v86 " + version + ", а встроена " + AppInfo.V86Version);

            int memory = Json.GetInt(meta, "memory_mb", 0);
            if (memory > 0 && memory != vm.MemoryMb)
                throw new Hv86Exception("снапшот сделан при " + memory + " МБ памяти, у ВМ сейчас " +
                                        vm.MemoryMb + " МБ: верните прежний размер или используйте другой снапшот");

            string diskCopy = Path.Combine(dir, "disk.img");
            string target = vm.HdaPath;

            if (Json.GetBool(meta, "disk_copy", false) && File.Exists(diskCopy))
            {
                if (!string.IsNullOrEmpty(target))
                {
                    try
                    {
                        AtomicFile.Move(diskCopy, target);
                    }
                    catch (Exception ex)
                    {
                        throw new Hv86Exception("не удалось восстановить образ диска: " + ex.Message);
                    }
                }
            }
            else if (!string.IsNullOrEmpty(target) && File.Exists(target))
            {
                // старые снапшоты без копии диска: сверяем размер, чтобы не подсунуть чужой образ
                long expected = Json.GetLong(meta, "disk_size", 0);
                long actual = new FileInfo(target).Length;
                if (expected > 0 && expected != actual)
                    throw new Hv86Exception("образ диска не совпадает со снапшотом: откат невозможен");
            }

            return state;
        }

        public static void Delete(VmDefinition vm, string snapshotId)
        {
            string dir = Path.Combine(Paths.VmSnapshotsDir(vm.Id), snapshotId);
            if (!Directory.Exists(dir)) throw new Hv86Exception("снапшот не найден: " + snapshotId);

            AtomicFile.MoveToTrash(dir, "snap-" + vm.Id + "-" + snapshotId);
            Log.Info("SNAP", "снапшот " + snapshotId + " удалён");
        }

        // ------------------------------------------------------- кольцо (ring)

        public static string SaveRing(VmDefinition vm, string stateTempFile, long instructions)
        {
            string dir = Paths.VmRingDir(vm.Id);
            Directory.CreateDirectory(dir);

            string stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            string name = "ring-" + stamp + ".bin";
            string target = Path.Combine(dir, name);

            long size = new FileInfo(stateTempFile).Length;
            AtomicFile.Move(stateTempFile, target);

            Dictionary<string, object> meta = Json.Obj(
                "created_utc", DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
                "size", size,
                "instructions", instructions,
                "v86_version", AppInfo.V86Version,
                "memory_mb", vm.MemoryMb,
                "disk_stamp", DiskStamp(vm));

            Json.WriteFile(target + ".json", meta);

            PruneRing(vm, out Exception _);
            UpdateRingIndex(vm);

            Log.Info("FT", "чекпоинт " + name + " (" + (size / 1024 / 1024) + " МБ)");
            return target;
        }

        /// <summary>Самый свежий валидный чекпоинт, подходящий текущему диску, или null.</summary>
        public static string NewestRing(VmDefinition vm)
        {
            try
            {
                string dir = Paths.VmRingDir(vm.Id);
                if (!Directory.Exists(dir)) return null;

                string[] files = Directory.GetFiles(dir, "ring-*.bin");
                if (files.Length == 0) return null;

                Array.Sort(files, (a, b) => string.Compare(b, a, StringComparison.Ordinal));

                string stamp = DiskStamp(vm);

                foreach (string file in files)
                {
                    Dictionary<string, object> meta = Json.ReadFile(file + ".json");
                    if (meta == null) continue;

                    if (Json.GetString(meta, "v86_version", AppInfo.V86Version) != AppInfo.V86Version) continue;
                    if (Json.GetInt(meta, "memory_mb", vm.MemoryMb) != vm.MemoryMb) continue;

                    string metaStamp = Json.GetString(meta, "disk_stamp", "");
                    if (!string.IsNullOrEmpty(metaStamp) && metaStamp != stamp) continue;

                    return file;
                }
            }
            catch (Exception ex)
            {
                Log.Exception("FT", "поиск свежего чекпоинта", ex);
            }

            return null;
        }

        public static void ClearRing(VmDefinition vm)
        {
            try
            {
                string dir = Paths.VmRingDir(vm.Id);
                if (!Directory.Exists(dir)) return;

                foreach (string file in Directory.GetFiles(dir))
                    try { File.Delete(file); } catch { }

                UpdateRingIndex(vm);
            }
            catch (Exception ex)
            {
                Log.Warn("FT", "очистка кольца: " + ex.Message);
            }
        }

        public static int RingCount(VmDefinition vm)
        {
            try
            {
                string dir = Paths.VmRingDir(vm.Id);
                if (!Directory.Exists(dir)) return 0;
                return Directory.GetFiles(dir, "ring-*.bin").Length;
            }
            catch { return 0; }
        }

        private static void PruneRing(VmDefinition vm, out Exception error)
        {
            error = null;

            try
            {
                string dir = Paths.VmRingDir(vm.Id);
                string[] files = Directory.GetFiles(dir, "ring-*.bin");
                Array.Sort(files, (a, b) => string.Compare(b, a, StringComparison.Ordinal));

                int keep = Math.Max(1, vm.FtKeep);

                for (int i = keep; i < files.Length; i++)
                {
                    try { File.Delete(files[i]); } catch { }
                    try { File.Delete(files[i] + ".json"); } catch { }
                }
            }
            catch (Exception ex)
            {
                error = ex;
                Log.Warn("FT", "чистка кольца: " + ex.Message);
            }
        }

        private static void UpdateRingIndex(VmDefinition vm)
        {
            try
            {
                List<object> index = new List<object>();
                string dir = Paths.VmRingDir(vm.Id);

                if (Directory.Exists(dir))
                    foreach (string file in Directory.GetFiles(dir, "ring-*.bin"))
                    {
                        long size = new FileInfo(file).Length;
                        Dictionary<string, object> meta = Json.ReadFile(file + ".json");

                        index.Add(Json.Obj(
                            "file", Path.GetFileName(file),
                            "size", size,
                            "created_utc", Json.GetString(meta, "created_utc", "")));
                    }

                Json.WriteFile(Path.Combine(Paths.VmDir(vm.Id), "checkpoints.json"),
                    Json.Obj("items", index, "keep", vm.FtKeep, "interval_ms", vm.FtIntervalMs));
            }
            catch
            {
                // индекс - удобство, не критично
            }
        }

        // --------------------------------------------------------------- утилиты

        private static Dictionary<string, object> View(Dictionary<string, object> meta, string dir)
        {
            string state = Path.Combine(dir, "state.bin");
            long size = 0;
            try { if (File.Exists(state)) size = new FileInfo(state).Length; } catch { }

            return Json.Obj(
                "id", Json.GetString(meta, "id", ""),
                "name", Json.GetString(meta, "name", ""),
                "kind", Json.GetString(meta, "kind", KindSnapshot),
                "created_utc", Json.GetString(meta, "created_utc", ""),
                "instructions", Json.GetLong(meta, "instructions", 0),
                "size", size,
                "disk_copy", Json.GetBool(meta, "disk_copy", false),
                "valid", Json.GetBool(meta, "valid", true));
        }

        private static string DiskStamp(VmDefinition vm)
        {
            try
            {
                string path = vm.HdaPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return "none";

                FileInfo info = new FileInfo(path);
                return info.Length.ToString(CultureInfo.InvariantCulture) + ":" +
                       info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture);
            }
            catch
            {
                return "none";
            }
        }

        private static long DiskSize(VmDefinition vm)
        {
            try
            {
                string path = vm.HdaPath;
                if (string.IsNullOrEmpty(path) || !File.Exists(path)) return 0;
                return new FileInfo(path).Length;
            }
            catch { return 0; }
        }

        private static int NextSequence(string root)
        {
            int max = 0;

            try
            {
                foreach (string dir in Directory.GetDirectories(root))
                {
                    string name = Path.GetFileName(dir);
                    int dash = name.IndexOf('-');
                    string number = dash > 0 ? name.Substring(0, dash) : name;

                    int value;
                    if (int.TryParse(number, NumberStyles.Integer, CultureInfo.InvariantCulture, out value) && value > max)
                        max = value;
                }
            }
            catch { }

            return max + 1;
        }

        private static string Slug(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (char c in name.Trim().ToLowerInvariant())
            {
                if (sb.Length >= 24) break;
                if (char.IsLetterOrDigit(c)) sb.Append(c);
                else if (c == ' ' || c == '-' || c == '_') sb.Append('-');
            }

            string result = sb.ToString().Trim('-');
            while (result.IndexOf("--", StringComparison.Ordinal) >= 0) result = result.Replace("--", "-");
            return result;
        }
    }
}