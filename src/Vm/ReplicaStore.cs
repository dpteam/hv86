using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>
    /// Принятые реплики: базовый образ эпохи + последний state.
    ///
    /// Раскладка (см. docs/replica-remote.md):
    ///   replica\&lt;vm&gt;\replica.json   описание эпохи и последнего state
    ///   replica\&lt;vm&gt;\disk.img       образ на момент старта ВМ (epoch)
    ///   replica\&lt;vm&gt;\state-&lt;seq&gt;.bin
    ///
    /// Почему пара «образ + state», а не журнал: state v86 кумулятивен (в нём все
    /// грязные блоки с момента старта), а файл образа на основном хосте во время
    /// работы не меняется. Значит их сумма - согласованный срез.
    /// </summary>
    public sealed class ReplicaStore
    {
        private readonly string _root;

        public ReplicaStore(string root)
        {
            _root = root;
        }

        /// <summary>Хранилище в каталоге данных hv86.</summary>
        public static ReplicaStore Default()
        {
            return new ReplicaStore(Path.Combine(Paths.Root, "replica"));
        }

        public string Root { get { return _root; } }

        public string Dir(string vmId)
        {
            if (!IsSafeId(vmId)) throw new Hv86Exception("некорректный id ВМ: " + vmId);

            return Path.Combine(_root, vmId);
        }

        /// <summary>Идентификатор ВМ - только буквы/цифры/дефис: он идёт в путь.</summary>
        public static bool IsSafeId(string vmId)
        {
            if (string.IsNullOrEmpty(vmId) || vmId.Length > 64) return false;

            foreach (char c in vmId)
            {
                if (char.IsLetterOrDigit(c) || c == '-') continue;
                return false;
            }

            return true;
        }

        public Dictionary<string, object> Read(string vmId)
        {
            string file = Path.Combine(Dir(vmId), "replica.json");
            if (!File.Exists(file)) return null;

            return Json.ReadFile(file);
        }

        /// <summary>
        /// Новая эпоха: принимает базовый образ целиком. Пишем в .part и
        /// подменяем только если хэш совпал - недописанный образ не станет базовым.
        /// </summary>
        public Dictionary<string, object> Begin(string vmId, int epoch, string name,
            string vmJson, long diskSize, string diskSha256, Stream body)
        {
            if (epoch <= 0) throw new Hv86Exception("epoch должен быть больше нуля");
            if (diskSize <= 0) throw new Hv86Exception("пустой образ диска");

            string dir = Dir(vmId);
            Directory.CreateDirectory(dir);

            string part = Path.Combine(dir, "disk.img.part");
            string target = Path.Combine(dir, "disk.img");

            string actual;

            try
            {
                actual = CopyHashed(body, part, diskSize);
            }
            catch
            {
                TryDelete(part);
                throw;
            }

            if (!string.IsNullOrEmpty(diskSha256) &&
                !string.Equals(actual, diskSha256, StringComparison.OrdinalIgnoreCase))
            {
                TryDelete(part);

                throw new ReplicaConflict("хэш образа не совпал: ожидался " + Short(diskSha256) +
                                          ", принят " + Short(actual));
            }

            if (File.Exists(target)) AtomicFile.Move(part, target);
            else File.Move(part, target);

            // старые состояния прошлой эпохи больше не подходят
            foreach (string stale in Directory.GetFiles(dir, "state-*.bin"))
                TryDelete(stale);

            Dictionary<string, object> meta = Json.Obj(
                "vm_id", vmId,
                "name", name ?? "",
                "epoch", epoch,
                "disk_size", diskSize,
                "disk_sha256", actual,
                "seq", 0L,
                "state_size", 0L,
                "state_sha256", "",
                "vm_json", vmJson ?? "",
                "updated_utc", DateTime.UtcNow.ToString("o"));

            Json.WriteFile(Path.Combine(dir, "replica.json"), meta);

            Log.Info("REPL", "эпоха " + epoch + " для " + vmId + ": образ " +
                             (diskSize / 1024 / 1024) + " МБ принят");

            return meta;
        }

        /// <summary>
        /// Принимает state. Возвращает meta; duplicate=true, если такой seq уже был
        /// (повтор после обрыва связи не должен ломать реплику).
        /// </summary>
        public Dictionary<string, object> PutState(string vmId, int epoch, long seq, Stream body,
            out bool duplicate)
        {
            duplicate = false;

            string dir = Dir(vmId);
            Dictionary<string, object> meta = Read(vmId);

            if (meta == null) throw new ReplicaConflict("реплика не начата: сначала begin");

            if (Json.GetInt(meta, "epoch", 0) != epoch)
                throw new ReplicaConflict("эпоха не совпадает: у реплики " +
                                          Json.GetInt(meta, "epoch", 0) + ", прислали " + epoch);

            if (seq <= 0) throw new Hv86Exception("seq должен быть больше нуля");

            if (seq <= Json.GetLong(meta, "seq", 0))
            {
                duplicate = true;
                return meta;
            }

            string part = Path.Combine(dir, "state-" + seq + ".bin.part");
            string target = Path.Combine(dir, "state-" + seq + ".bin");

            long size;
            string sha;

            try
            {
                size = CopyHashed(body, part, -1, out sha);

                if (File.Exists(target)) AtomicFile.Move(part, target);
                else File.Move(part, target);
            }
            catch
            {
                TryDelete(part);
                throw;
            }

            meta["seq"] = seq;
            meta["state_size"] = size;
            meta["state_sha256"] = sha;
            meta["updated_utc"] = DateTime.UtcNow.ToString("o");

            Json.WriteFile(Path.Combine(dir, "replica.json"), meta);

            // держим только два последних состояния: старое может понадобиться,
            // если новое окажется битым, но копить их незачем
            foreach (string file in Directory.GetFiles(dir, "state-*.bin"))
            {
                string name = Path.GetFileNameWithoutExtension(file);
                long number;

                if (!long.TryParse(name.Substring("state-".Length), out number)) continue;
                if (number <= seq - 2) TryDelete(file);
            }

            Log.Info("REPL", "state " + seq + " для " + vmId + ": " + (size / 1024 / 1024) + " МБ");

            return meta;
        }

        public Dictionary<string, object> Status(string vmId)
        {
            Dictionary<string, object> meta = Read(vmId);

            if (meta == null)
                throw new ReplicaConflict("реплика не найдена: " + vmId);

            long updated = 0;
            DateTime updatedUtc;

            if (DateTime.TryParse(Json.GetString(meta, "updated_utc", ""), out updatedUtc))
                updated = (long)Math.Max(0, (DateTime.UtcNow - updatedUtc).TotalSeconds);

            Dictionary<string, object> view = Json.Obj(
                "vm_id", vmId,
                "name", Json.GetString(meta, "name", ""),
                "epoch", Json.GetInt(meta, "epoch", 0),
                "seq", Json.GetLong(meta, "seq", 0),
                "size", Json.GetLong(meta, "state_size", 0),
                "age_sec", updated,
                "warm", Json.GetLong(meta, "seq", 0) > 0,
                "updated_utc", Json.GetString(meta, "updated_utc", ""));

            return view;
        }

        public void Delete(string vmId)
        {
            string dir = Dir(vmId);

            if (Directory.Exists(dir)) AtomicFile.MoveToTrash(dir, "replica-" + vmId);

            Log.Info("REPL", "реплика " + vmId + " снята с учёта");
        }

        // ------------------------------------------------------------ служебное

        private static string CopyHashed(Stream source, string target, long expected)
        {
            string sha;
            CopyHashed(source, target, expected, out sha);
            return sha;
        }

        /// <summary>
        /// Копирует поток в файл, считая SHA-256 на лету. expected = -1 - размер
        /// неизвестен (state), иначе сверяем длину: обрезанный образ не примем.
        /// </summary>
        private static long CopyHashed(Stream source, string target, long expected, out string sha256)
        {
            sha256 = null;

            using (FileStream output = new FileStream(target, FileMode.Create, FileAccess.Write,
                       FileShare.None, 1 << 16, FileOptions.SequentialScan))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] buffer = new byte[1 << 16];
                long total = 0;

                while (true)
                {
                    int read = source.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;

                    output.Write(buffer, 0, read);
                    sha.TransformBlock(buffer, 0, read, null, 0);
                    total += read;

                    if (expected > 0 && total > expected)
                        throw new Hv86Exception("тело больше заявленного размера");
                }

                sha.TransformFinalBlock(new byte[0], 0, 0);

                if (expected > 0 && total != expected)
                    throw new Hv86Exception("тело короче заявленного размера: " + total + " из " + expected);

                sha256 = ToHex(sha.Hash);
                return total;
            }
        }

        private static string ToHex(byte[] bytes)
        {
            StringBuilder text = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes) text.Append(b.ToString("x2"));
            return text.ToString();
        }

        private static string Short(string hash)
        {
            if (string.IsNullOrEmpty(hash)) return "(нет)";
            return hash.Length <= 12 ? hash : hash.Substring(0, 12) + "…";
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    /// <summary>Конфликт протокола: реплика не принимает это состояние (HTTP 409).</summary>
    public sealed class ReplicaConflict : Hv86Exception
    {
        public ReplicaConflict(string message) : base(message) { }
    }
}