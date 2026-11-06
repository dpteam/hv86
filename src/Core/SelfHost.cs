using System;
using System.Diagnostics;
using System.IO;

namespace Hv86.Core
{
    /// <summary>
    /// Как запустить «самого себя» (процесс vmhost).
    ///
    /// Обычно это hv86.exe. Но при отладке и в тестах приложение запускают
    /// как `dotnet hv86.dll`, и тогда ProcessPath — это dotnet.exe: если
    /// запускать hv86.exe напрямую, сработает манифест requireAdministrator
    /// и без прав администратора процесс просто не создастся.
    /// </summary>
    public static class SelfHost
    {
        public static string FileName
        {
            get
            {
                string path = Environment.ProcessPath;

                if (!string.IsNullOrEmpty(path) &&
                    path.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
                    return path;

                if (!string.IsNullOrEmpty(path)) return path;
                return Paths.ExePath;
            }
        }

        /// <summary>Аргументы для дочернего процесса: префикс + переданный хвост.</summary>
        public static string Arguments(string tail)
        {
            string path = Environment.ProcessPath;

            if (!string.IsNullOrEmpty(path) &&
                path.EndsWith("dotnet.exe", StringComparison.OrdinalIgnoreCase))
            {
                // при запуске `dotnet hv86.dll` первый аргумент командной строки
                // и есть путь к нашей сборке. Assembly.Location в single-file
                // пустой (IL3000), поэтому берём именно argv[0].
                string dll = null;

                try
                {
                    string[] args = Environment.GetCommandLineArgs();
                    if (args != null && args.Length > 0) dll = args[0];
                }
                catch
                {
                    // ниже сработает запасной путь
                }

                if (string.IsNullOrEmpty(dll) || !dll.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    dll = Path.Combine(Paths.BaseDir, "hv86.dll");

                return "\"" + dll + "\" " + tail;
            }

            return tail;
        }

        public static string Describe()
        {
            return FileName + " " + Arguments("<args>");
        }
    }
}
