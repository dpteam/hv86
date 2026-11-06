using System;
using System.IO;

namespace Hv86.Core
{
    /// <summary>
    /// Все пути приложения в одном месте.
    ///
    /// Данные живут в %ProgramData%\hv86, потому что их читает и служба
    /// (LocalSystem), и интерфейс пользователя, и процессы ВМ. Если положить
    /// их в %LocalAppData%, у службы это будет профиль SYSTEM и она увидит
    /// совсем другой набор ВМ - классическая ловушка.
    ///
    /// Портативный режим (portable.txt рядом с exe) переносит данные в
    /// .\data - удобно для тестов и для флешки, но службе он не подходит.
    /// </summary>
    public static class Paths
    {
        private static readonly object Gate = new object();
        private static bool _resolved;
        private static string _root;
        private static bool _portable;

        public static string Root
        {
            get { Resolve(); return _root; }
        }

        public static bool IsPortable
        {
            get { Resolve(); return _portable; }
        }

        public static string BaseDir
        {
            get
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory;
                if (string.IsNullOrEmpty(dir)) return ".\\";
                return dir.EndsWith("\\", StringComparison.Ordinal) ? dir : dir + "\\";
            }
        }

        /// <summary>
        /// Каталог headless-движка (engine\vmhost.mjs). В отладочной раскладке
        /// exe лежит в bin\, а engine - на уровень выше.
        /// </summary>
        public static string EngineDir
        {
            get
            {
                string local = Path.Combine(BaseDir, "engine");
                if (File.Exists(Path.Combine(local, "vmhost.mjs"))) return local;

                try
                {
                    string parent = Path.GetFullPath(Path.Combine(BaseDir, ".."));
                    string up = Path.Combine(parent, "engine");
                    if (File.Exists(Path.Combine(up, "vmhost.mjs"))) return up;
                }
                catch { }

                return local;
            }
        }

        public static string ExePath
        {
            get { return Path.Combine(BaseDir, "hv86.exe"); }
        }

        public static string LogDir { get { return Path.Combine(Root, "logs"); } }
        public static string VmsDir { get { return Path.Combine(Root, "vms"); } }
        public static string CacheDir { get { return Path.Combine(Root, "cache"); } }
        public static string PresetCacheDir { get { return Path.Combine(CacheDir, "presets"); } }
        public static string TrashDir { get { return Path.Combine(Root, "trash"); } }
        public static string WebViewDataDir { get { return Path.Combine(Root, "webview2"); } }

        public static string VmDir(string id) { return Path.Combine(VmsDir, id); }
        public static string VmFile(string id) { return Path.Combine(VmDir(id), "vm.json"); }
        public static string VmDisksDir(string id) { return Path.Combine(VmDir(id), "disks"); }
        public static string VmMediaDir(string id) { return Path.Combine(VmDir(id), "media"); }
        public static string VmSnapshotsDir(string id) { return Path.Combine(VmDir(id), "snapshots"); }
        public static string VmRingDir(string id) { return Path.Combine(VmDir(id), "checkpoints"); }

        public static void EnsureDirectories()
        {
            SafeCreate(Root);
            SafeCreate(LogDir);
            SafeCreate(VmsDir);
            SafeCreate(CacheDir);
            SafeCreate(PresetCacheDir);
            SafeCreate(TrashDir);
        }

        public static void EnsureVmDirectories(string id)
        {
            SafeCreate(VmDir(id));
            SafeCreate(VmDisksDir(id));
            SafeCreate(VmMediaDir(id));
            SafeCreate(VmSnapshotsDir(id));
            SafeCreate(VmRingDir(id));
        }

        /// <summary>Сброс кэша (нужно тестам, которые подменяют окружение).</summary>
        public static void Reset()
        {
            lock (Gate) { _resolved = false; }
        }

        /// <summary>
        /// Переносит данные рядом с exe. Используется тестами и портативной
        /// сборкой: служба при этом не работает, но VM-хосты - да.
        /// </summary>
        public static void ForcePortable(string subdir)
        {
            lock (Gate)
            {
                _portable = true;
                _root = Path.Combine(BaseDir, string.IsNullOrEmpty(subdir) ? "data" : subdir);
                _resolved = true;
            }
        }

        /// <summary>Абсолютный корень данных (для самотестов во временном каталоге).</summary>
        public static void ForceRoot(string absoluteRoot)
        {
            lock (Gate)
            {
                _portable = true;
                _root = absoluteRoot;
                _resolved = true;
            }
        }

        private static void Resolve()
        {
            lock (Gate)
            {
                if (_resolved) return;

                // переопределение корня: тесты и переносимый режим.
                // Наследуется дочерними процессами (vmhost), поэтому все
                // участники видят один и тот же набор ВМ.
                string overrideRoot = Environment.GetEnvironmentVariable("HV86_ROOT");
                if (!string.IsNullOrEmpty(overrideRoot))
                {
                    _portable = true;
                    _root = Path.GetFullPath(overrideRoot);
                    _resolved = true;
                    return;
                }

                string portableFlag = Path.Combine(BaseDir, "portable.txt");
                _portable = File.Exists(portableFlag);

                _root = _portable
                    ? Path.Combine(BaseDir, "data")
                    : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "hv86");

                _resolved = true;
            }
        }

        private static void SafeCreate(string dir)
        {
            try { if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir); }
            catch { }
        }

        /// <summary>Относительный путь внутри каталога ВМ (то, что пишется в vm.json).</summary>
        public static string Relative(string vmId, string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return null;
            try
            {
                string rel = Path.GetRelativePath(VmDir(vmId), absolutePath);
                return rel.Replace('/', '\\');
            }
            catch
            {
                return absolutePath;
            }
        }

        /// <summary>Абсолютный путь из vm.json. Пусто/нет - null.</summary>
        public static string Absolute(string vmId, string relativePath)
        {
            if (string.IsNullOrEmpty(relativePath)) return null;
            try
            {
                if (Path.IsPathRooted(relativePath)) return relativePath;
                return Path.GetFullPath(Path.Combine(VmDir(vmId), relativePath));
            }
            catch
            {
                return null;
            }
        }

        public static bool LooksLikePath(string value)
        {
            if (string.IsNullOrEmpty(value)) return false;
            return value.IndexOf('\\') >= 0 || value.IndexOf('/') >= 0 || value.IndexOf(':') >= 0;
        }
    }
}