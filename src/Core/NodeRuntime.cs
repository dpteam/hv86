using System;
using System.Diagnostics;
using System.IO;
using System.Text;

namespace Hv86.Core
{
    /// <summary>
    /// Поиск Node.js для headless-движка.
    ///
    /// Служба запускается от LocalSystem, и её PATH — не PATH пользователя,
    /// поэтому «просто вызвать node» нельзя: ищем по порядку
    /// HV86_NODE → PATH → Program Files → engine\node.
    ///
    /// Требуется Node 20+: v86 использует WebSocket и современный ESM,
    /// на старых версиях движок просто не стартует.
    /// </summary>
    public static class NodeRuntime
    {
        private static readonly object Gate = new object();
        private static string _path;
        private static bool _resolved;
        private static string _error;

        public static string Executable
        {
            get { lock (Gate) { Resolve(); return _path; } }
        }

        public static string Error
        {
            get { lock (Gate) { Resolve(); return _error; } }
        }

        public static void Reset()
        {
            lock (Gate)
            {
                _resolved = false;
                _path = null;
                _error = null;
            }
        }

        private static void Resolve()
        {
            if (_resolved) return;

            _resolved = true;

            string explicitPath = Environment.GetEnvironmentVariable("HV86_NODE");
            if (!string.IsNullOrEmpty(explicitPath) && TryNode(explicitPath, out string explicitVersion))
            {
                _path = explicitPath;
                Log.Info("NODE", "из HV86_NODE: " + explicitPath + " (" + explicitVersion + ")");
                return;
            }

            foreach (string candidate in Candidates())
            {
                if (TryNode(candidate, out string version))
                {
                    _path = candidate;
                    Log.Info("NODE", "найден: " + candidate + " (" + version + ")");
                    return;
                }
            }

            _error = "Node.js не найден (нужен 20+). Установите Node или положите node.exe в " +
                     Path.Combine(Paths.BaseDir, "engine", "node") + ", либо задайте HV86_NODE.";
        }

        private static System.Collections.Generic.IEnumerable<string> Candidates()
        {
            // 1. PATH
            string fromPath = Where("node.exe");
            if (!string.IsNullOrEmpty(fromPath)) yield return fromPath;

            // 2. стандартные места установки
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            if (!string.IsNullOrEmpty(programFiles))
            {
                yield return Path.Combine(programFiles, "nodejs", "node.exe");
                yield return Path.Combine(programFiles, "node", "node.exe");
            }

            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrEmpty(programFilesX86))
                yield return Path.Combine(programFilesX86, "nodejs", "node.exe");

            // 3. портативный вариант рядом с exe
            yield return Path.Combine(Paths.BaseDir, "engine", "node", "node.exe");
            yield return Path.Combine(Paths.BaseDir, "engine", "node", "node");
        }

        private static string Where(string fileName)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = "/c where " + fileName,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8
                };

                using (Process process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);

                    foreach (string line in output.Split('\n'))
                    {
                        string trimmed = line.Trim();
                        if (trimmed.Length > 0 && File.Exists(trimmed)) return trimmed;
                    }
                }
            }
            catch
            {
                // нет PATH - не беда, есть другие варианты
            }

            return null;
        }

        private static bool TryNode(string path, out string version)
        {
            version = null;

            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return false;

            try
            {
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = path,
                    Arguments = "--version",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };

                using (Process process = Process.Start(psi))
                {
                    string output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(5000);

                    if (process.ExitCode != 0 || string.IsNullOrEmpty(output)) return false;

                    version = output;

                    // v20+ : "v20.11.0"
                    string digits = output.TrimStart('v');
                    int dot = digits.IndexOf('.');

                    if (dot > 0 && int.TryParse(digits.Substring(0, dot), out int major))
                        return major >= 20;

                    return false;
                }
            }
            catch
            {
                return false;
            }
        }
    }
}