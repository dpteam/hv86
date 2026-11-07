using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>
    /// Мост к внешнему 7-Zip.
    ///
    /// RAR и 7z в BCL не читаются, а писать свой распаковщик ради каталога
    /// образов - это месяцы работы и лицензионные тонкости. Поэтому берём
    /// установленный `7z.exe`, если он есть, а если нет - честно отказываем
    /// с инструкцией, где его взять. Так hv86 остаётся без обязательных
    /// зависимостей: без 7-Zip всё работает как раньше, кроме RAR/7z.
    ///
    /// Порядок поиска: HV86_7Z -> рядом с hv86.exe (и в tools\) -> PATH ->
    /// каталог установки 7-Zip. Результат запоминается на процесс: искать
    /// файл на каждую распаковку незачем.
    /// </summary>
    public static class SevenZip
    {
        public const string EnvVar = "HV86_7Z";

        private static readonly string[] ToolNames = { "7z.exe", "7za.exe", "7zr.exe" };

        private static bool _searched;
        private static string _tool;

        /// <summary>Путь к 7z.exe или null, если его нет.</summary>
        public static string FindTool()
        {
            if (_searched) return _tool;

            _searched = true;
            _tool = Locate();

            if (_tool != null) Log.Info("PRESET", "7-Zip: " + _tool);
            else Log.Info("PRESET", "7-Zip не найден: RAR/7z распаковать нечем");

            return _tool;
        }

        public static bool Available { get { return FindTool() != null; } }

        private static string Locate()
        {
            string explicitPath = Environment.GetEnvironmentVariable(EnvVar);
            if (!string.IsNullOrEmpty(explicitPath) && File.Exists(explicitPath)) return explicitPath;

            List<string> dirs = new List<string>();

            try { dirs.Add(AppContext.BaseDirectory); } catch { }

            string appDir = null;
            try { appDir = Path.GetDirectoryName(typeof(SevenZip).Assembly.Location); } catch { }
            if (!string.IsNullOrEmpty(appDir)) dirs.Add(appDir);

            if (!string.IsNullOrEmpty(appDir)) dirs.Add(Path.Combine(appDir, "tools"));

            foreach (string dir in dirs)
            {
                if (string.IsNullOrEmpty(dir)) continue;

                foreach (string name in ToolNames)
                {
                    string candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate)) return candidate;
                }
            }

            string pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";

            foreach (string raw in pathVariable.Split(';'))
            {
                string dir = raw.Trim();
                if (dir.Length == 0) continue;

                foreach (string name in ToolNames)
                {
                    try
                    {
                        string candidate = Path.Combine(dir, name);
                        if (File.Exists(candidate)) return candidate;
                    }
                    catch
                    {
                        // в PATH бывают кавычки и мусор - пропускаем
                    }
                }
            }

            foreach (string programFiles in new[]
                     {
                         Environment.GetEnvironmentVariable("ProgramFiles"),
                         Environment.GetEnvironmentVariable("ProgramFiles(x86)")
                     })
            {
                if (string.IsNullOrEmpty(programFiles)) continue;

                string candidate = Path.Combine(programFiles, "7-Zip", "7z.exe");
                if (File.Exists(candidate)) return candidate;
            }

            return null;
        }

        /// <summary>Запись архива: имя внутри архива и распакованный размер.</summary>
        public sealed class Entry
        {
            public string Name;
            public long Size;
            public bool IsFolder;
        }

        /// <summary>
        /// Список файлов архива. Читаем `7z l -slt`: там поля по строкам `Key = value`,
        /// а имена с пробелами и скобками не превращаются в кашу (в отличие от
        /// обычного вывода, где колонки разделены пробелами).
        /// </summary>
        public static List<Entry> List(string archive)
        {
            string tool = RequireTool();

            string output = Run(tool, new[] { "l", "-slt", "-sccUTF-8", archive }, 120000);

            List<Entry> entries = new List<Entry>();
            Entry current = null;
            bool inList = false;

            foreach (string raw in output.Split('\n'))
            {
                string line = raw.TrimEnd('\r');

                // шапка архива отделена от списка файлов строкой из дефисов
                if (line.StartsWith("----------", StringComparison.Ordinal))
                {
                    inList = true;
                    continue;
                }

                if (!inList) continue;

                if (line.StartsWith("Path = ", StringComparison.Ordinal))
                {
                    if (current != null) entries.Add(current);

                    current = new Entry { Name = line.Substring(7) };
                    continue;
                }

                if (current == null) continue;

                if (line.StartsWith("Size = ", StringComparison.Ordinal))
                {
                    long size;
                    if (long.TryParse(line.Substring(7).Trim(), out size)) current.Size = size;
                }
                else if (line.StartsWith("Folder = ", StringComparison.Ordinal))
                {
                    current.IsFolder = line.Substring(9).Trim() == "+";
                }
                else if (line.StartsWith("Attributes = ", StringComparison.Ordinal))
                {
                    string attributes = line.Substring(13).Trim();

                    // "D" первым символом - каталог (у файлов там A/RA/…)
                    if (attributes.StartsWith("D", StringComparison.Ordinal)) current.IsFolder = true;
                }
            }

            if (current != null) entries.Add(current);

            return entries;
        }

        /// <summary>Распаковывает архив целиком в каталог.</summary>
        public static void ExtractAll(string archive, string targetDir)
        {
            string tool = RequireTool();

            Directory.CreateDirectory(targetDir);

            Run(tool, new[] { "x", "-y", "-sccUTF-8", "-o" + targetDir, archive }, 1800000);
        }

        private static string RequireTool()
        {
            string tool = FindTool();

            if (tool == null)
                throw new Hv86Exception("архив RAR/7z: нужен 7-Zip. Установите его, положите 7z.exe " +
                                        "рядом с hv86.exe или укажите путь в переменной " + EnvVar);

            return tool;
        }

        /// <summary>
        /// Запускает 7z и возвращает stdout. Процесс живёт в Job Object, поэтому
        /// не переживёт hv86, даже если зависнет на битом архиве.
        /// </summary>
        private static string Run(string tool, string[] arguments, int timeoutMs)
        {
            ProcessStartInfo info = new ProcessStartInfo
            {
                FileName = tool,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            foreach (string argument in arguments) info.ArgumentList.Add(argument);

            StringBuilder output = new StringBuilder();
            StringBuilder errors = new StringBuilder();

            using (Process process = new Process())
            using (JobObject job = new JobObject(null))
            {
                process.StartInfo = info;
                process.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null) output.AppendLine(e.Data);
                };
                process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null) errors.AppendLine(e.Data);
                };

                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    throw new Hv86Exception("не удалось запустить 7-Zip (" + tool + "): " + ex.Message);
                }

                job.Assign(process);

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(true); } catch { }

                    throw new Hv86Exception("7-Zip не уложился в " + (timeoutMs / 1000) + " с: " +
                                            Path.GetFileName(arguments[arguments.Length - 1]));
                }

                // дождаться хвоста асинхронных чтений
                process.WaitForExit();

                // 0 - всё хорошо, 1 - предупреждение (часть файлов не распакована):
                // для нас это не повод падать, если образ всё-таки есть
                if (process.ExitCode > 1)
                    throw new Hv86Exception("7-Zip завершился с кодом " + process.ExitCode + ": " +
                                            FirstLine(errors.ToString()));
            }

            return output.ToString();
        }

        private static string FirstLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return "";

            foreach (string line in text.Split('\n'))
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0) return trimmed;
            }

            return "";
        }
    }
}