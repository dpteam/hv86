using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Hv86.Core;
using Hv86.Vm;

namespace Hv86.App
{
    /// <summary>
    /// `host.diagnostics`: один zip, который можно отдать человеку вместо
    /// десяти сообщений «а у меня не работает».
    ///
    /// Собираем только то, что не секретно: логи службы/движка/ВМ, описание
    /// ВМ, статус хоста и окружение. Образы дисков и state-файлы не входят.
    /// </summary>
    public static class Diagnostics
    {
        public static Dictionary<string, object> Collect(Dictionary<string, object> hostStatus, List<object> vms)
        {
            Paths.EnsureDirectories();

            string directory = Path.Combine(Paths.Root, "diagnostics");
            Directory.CreateDirectory(directory);

            string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            string path = Path.Combine(directory, "hv86-diag-" + stamp + ".zip");

            StringBuilder system = new StringBuilder();
            system.AppendLine(AppInfo.Summary());
            system.AppendLine("utc: " + DateTime.UtcNow.ToString("o"));
            system.AppendLine("os: " + Environment.OSVersion.VersionString);
            system.AppendLine("64bit os: " + Environment.Is64BitOperatingSystem);
            system.AppendLine("64bit process: " + Environment.Is64BitProcess);
            system.AppendLine("machine: " + Environment.MachineName);
            system.AppendLine("user: " + Elevation.CurrentUser);
            system.AppendLine("elevated: " + Elevation.IsElevated);
            system.AppendLine("session: " + System.Diagnostics.Process.GetCurrentProcess().SessionId);
            system.AppendLine("active console session: " + SessionLauncher.ActiveConsoleSessionId);
            system.AppendLine("data: " + Paths.Root);
            system.AppendLine("portable: " + Paths.IsPortable);
            system.AppendLine("self: " + SelfHost.Describe());
            system.AppendLine("cpus: " + Environment.ProcessorCount);
            system.AppendLine("runtime: " + Environment.Version);
            system.AppendLine();

            try
            {
                system.AppendLine("webview2: " +
                    Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString());
            }
            catch (Exception ex)
            {
                system.AppendLine("webview2: not available - " + ex.Message);
            }

            int logs = 0;

            using (FileStream stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (ZipArchive archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                Write(archive, "system.txt", system.ToString());
                Write(archive, "host.json", Json.Pretty(hostStatus));
                Write(archive, "vms.json", Json.Pretty(vms));

                if (Directory.Exists(Paths.LogDir))
                {
                    foreach (string file in Directory.GetFiles(Paths.LogDir, "*.log"))
                    {
                        // хвост лога: полный файл за месяц никому не нужен
                        string text = ReadTail(file, 512 * 1024);

                        Write(archive, "logs/" + Path.GetFileName(file), text);
                        logs++;
                    }
                }

                // описание каждой ВМ без образов и state
                foreach (object item in vms ?? new List<object>())
                {
                    Dictionary<string, object> view = item as Dictionary<string, object>;
                    string id = Json.GetString(view, "id", "");
                    if (!VmStore.IsValidId(id)) continue;

                    Dictionary<string, object> definition = Json.ReadFile(Paths.VmFile(id));
                    if (definition != null) Write(archive, "vms/" + id + "/vm.json", Json.Pretty(definition));

                    string index = Paths.VmFile(id).Replace("vm.json", "checkpoints.json");
                    if (File.Exists(index)) Write(archive, "vms/" + id + "/checkpoints.json", File.ReadAllText(index));
                }
            }

            long size = new FileInfo(path).Length;
            Log.Info("DIAG", "диагностика: " + path + " (" + (size / 1024) + " КБ, логов " + logs + ")");

            return Json.Obj("path", path, "size", size, "logs", logs);
        }

        private static void Write(ZipArchive archive, string name, string text)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);

            using (StreamWriter writer = new StreamWriter(entry.Open(), new UTF8Encoding(false)))
                writer.Write(text ?? "");
        }

        private static string ReadTail(string file, int maxBytes)
        {
            try
            {
                using (FileStream stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    long start = Math.Max(0, stream.Length - maxBytes);
                    stream.Seek(start, SeekOrigin.Begin);

                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8))
                    {
                        string text = reader.ReadToEnd();
                        return start > 0 ? "...(обрезано)\n" + text : text;
                    }
                }
            }
            catch (Exception ex)
            {
                return "log read failed: " + ex.Message;
            }
        }
    }
}