using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Hv86.Core
{
    public enum LogLevel
    {
        Debug = 0,
        Info = 1,
        Warn = 2,
        Error = 3
    }

    /// <summary>
    /// Лог в %ProgramData%\hv86\logs\hv86-&lt;role&gt;-&lt;дата&gt;.log.
    ///
    /// Роль (svc / ui / vm) задаётся на старте процесса: в одном логе смешивать
    /// службу, интерфейс и процессы ВМ неудобно - они живут в разных сессиях
    /// и пишут одновременно.
    ///
    /// Никогда не бросает: сломанный лог не должен ронять гипервизор.
    /// </summary>
    public static class Log
    {
        private static readonly object Gate = new object();
        private static string _role = "app";

        public static LogLevel MinLevel = LogLevel.Info;

        public static string Role
        {
            get { return _role; }
            set { if (!string.IsNullOrEmpty(value)) _role = value; }
        }

        public static string CurrentFile
        {
            get
            {
                string day = DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                return Path.Combine(Paths.LogDir, "hv86-" + _role + "-" + day + ".log");
            }
        }

        public static void Debug(string category, string message) { Write(LogLevel.Debug, category, message); }
        public static void Info(string category, string message) { Write(LogLevel.Info, category, message); }
        public static void Warn(string category, string message) { Write(LogLevel.Warn, category, message); }
        public static void Error(string category, string message) { Write(LogLevel.Error, category, message); }

        public static void Exception(string category, string message, Exception ex)
        {
            if (ex == null) { Error(category, message); return; }
            Write(LogLevel.Error, category, message + ": " + ex.GetType().Name + ": " + ex.Message);
            Write(LogLevel.Debug, category, ex.ToString());
        }

        private static void Write(LogLevel level, string category, string message)
        {
            if (level < MinLevel) return;

            string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "] " +
                          Tag(level) + " [" + (category ?? "-") + "] " + (message ?? "");

            try
            {
                lock (Gate)
                {
                    Paths.EnsureDirectories();
                    File.AppendAllText(CurrentFile, line + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch
            {
                // лог не должен ломать работу
            }
        }

        /// <summary>Хвост лога для UI/диагностики.</summary>
        public static string Tail(int lines)
        {
            try
            {
                lock (Gate)
                {
                    string file = CurrentFile;
                    if (!File.Exists(file)) return string.Empty;

                    string[] all = File.ReadAllLines(file, Encoding.UTF8);
                    int from = Math.Max(0, all.Length - Math.Max(1, lines));
                    StringBuilder sb = new StringBuilder();
                    for (int i = from; i < all.Length; i++) sb.AppendLine(all[i]);
                    return sb.ToString();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string Tag(LogLevel level)
        {
            switch (level)
            {
                case LogLevel.Debug: return "DBG";
                case LogLevel.Warn: return "WRN";
                case LogLevel.Error: return "ERR";
                default: return "INF";
            }
        }
    }
}