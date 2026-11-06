using System;
using System.IO;
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