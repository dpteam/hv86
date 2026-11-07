using System;
using System.Collections.Generic;
using System.IO;

namespace Hv86.Core
{
    /// <summary>
    /// Настройки хоста (`%ProgramData%\hv86\hv86.json`).
    ///
    /// Здесь только то, что относится к самому hv86, а не к конкретной ВМ:
    /// шрифт текстового режима по умолчанию. ВМ может переопределить его у себя
    /// (`vm.json` → `text`), а пустое значение означает «как на хосте».
    ///
    /// Файл читается и пишется атомарно (`AtomicFile`), формат — обычный JSON;
    /// незнакомые ключи сохраняются как есть, чтобы не терять чужие настройки.
    /// </summary>
    public static class HostSettings
    {
        /// <summary>Семейство шрифта текстового режима; пусто - встроенный monospace.</summary>
        public static string TextFont
        {
            get { return (string)Get("text_font", "") ?? ""; }
            set { Set("text_font", value ?? ""); }
        }

        /// <summary>Размер шрифта в px; 0 - как задано в CSS консоли (14px).</summary>
        public static int TextFontSize
        {
            get { return Convert.ToInt32(Get("text_font_size", 0), System.Globalization.CultureInfo.InvariantCulture); }
            set { Set("text_font_size", (long)Math.Max(0, Math.Min(48, value))); }
        }

        /// <summary>
        /// Чем отпускать захват мыши в консоли: f8f12 (как в 86box), middle,
        /// esc, ctrlalt. Захват только по кнопке - случайно не включается.
        /// </summary>
        public static string CaptureRelease
        {
            get { return NormalizeCapture(Get("capture_release", "f8f12") as string); }
            set { Set("capture_release", NormalizeCapture(value)); }
        }

        public static string NormalizeCapture(string mode)
        {
            switch ((mode ?? "").Trim().ToLowerInvariant())
            {
                case "middle": return "middle";
                case "esc": return "esc";
                case "ctrlalt": return "ctrlalt";
                default: return "f8f12";
            }
        }

        /// <summary>
        /// Порт, на котором этот хост принимает реплики с другой машины
        /// (ft.mode = remote у ВМ на основном хосте). 0 - не принимать.
        /// </summary>
        public static int ReplicaPort
        {
            get { return (int)Math.Max(0, Math.Min(65535,
                Convert.ToInt32(Get("replica_port", 0), System.Globalization.CultureInfo.InvariantCulture))); }
            set { Set("replica_port", (long)Math.Max(0, Math.Min(65535, value))); }
        }

        /// <summary>Общий секрет с основным хостом; без него реплика ничего не примет.</summary>
        public static string ReplicaToken
        {
            get { return (string)Get("replica_token", "") ?? ""; }
            set { Set("replica_token", value ?? ""); }
        }

        /// <summary>
        /// Адрес свидетеля (docs/replica-quorum.md). Нужен, чтобы этот хост
        /// проверял аренду у принятых состояний и мог поднимать реплику сам.
        /// </summary>
        public static string WitnessUrl
        {
            get { return (string)Get("witness_url", "") ?? ""; }
            set { Set("witness_url", value ?? ""); }
        }

        /// <summary>Общий секрет со свидетелем.</summary>
        public static string WitnessToken
        {
            get { return (string)Get("witness_token", "") ?? ""; }
            set { Set("witness_token", value ?? ""); }
        }

        /// <summary>
        /// Захватывать мышь кликом по экрану консоли (не только кнопкой).
        /// Работает, когда гостевой драйвер включил мышь.
        /// </summary>
        public static bool CaptureOnClick
        {
            get { return !"false".Equals(Get("capture_on_click", "true") as string, StringComparison.OrdinalIgnoreCase); }
            set { Set("capture_on_click", value ? "true" : "false"); }
        }

        /// <summary>
        /// Загруженные пользователем шрифты: [{family, file}]. Файлы лежат в
        /// %HV86_ROOT%\fonts, здесь только описание.
        /// </summary>
        public static List<Dictionary<string, object>> Fonts()
        {
            List<Dictionary<string, object>> list = new List<Dictionary<string, object>>();

            lock (Gate)
            {
                object raw;

                if (!Load().TryGetValue("fonts", out raw) || raw == null) return list;

                List<object> items = raw as List<object>;

                if (items == null) return list;

                foreach (object item in items)
                {
                    Dictionary<string, object> entry = item as Dictionary<string, object>;
                    if (entry == null) continue;

                    string file = Json.GetString(entry, "file", "");
                    string family = Json.GetString(entry, "family", "");

                    if (file.Length == 0 || family.Length == 0) continue;

                    list.Add(Json.Obj("family", family, "file", file));
                }
            }

            return list;
        }

        public static void Fonts(List<Dictionary<string, object>> list)
        {
            List<object> items = new List<object>();

            foreach (Dictionary<string, object> item in list ?? new List<Dictionary<string, object>>())
                items.Add(Json.Obj(
                    "family", Json.GetString(item, "family", ""),
                    "file", Json.GetString(item, "file", "")));

            Set("fonts", items);
        }

        // ------------------------------------------------------------- хранение

        private static readonly object Gate = new object();
        private static Dictionary<string, object> _cache;

        private static string FilePath
        {
            get { return Path.Combine(Paths.Root, "hv86.json"); }
        }

        private static Dictionary<string, object> Load()
        {
            lock (Gate)
            {
                if (_cache != null) return _cache;

                try
                {
                    _cache = Json.ReadFile(FilePath) ?? new Dictionary<string, object>(StringComparer.Ordinal);
                }
                catch (Exception ex)
                {
                    Log.Warn("CFG", "hv86.json: " + ex.Message);
                    _cache = new Dictionary<string, object>(StringComparer.Ordinal);
                }

                return _cache;
            }
        }

        private static object Get(string key, object fallback)
        {
            lock (Gate)
            {
                object value;

                return Load().TryGetValue(key, out value) && value != null ? value : fallback;
            }
        }

        private static void Set(string key, object value)
        {
            lock (Gate)
            {
                Load()[key] = value;

                try
                {
                    Paths.EnsureDirectories();
                    Json.WriteFile(FilePath, _cache);
                }
                catch (Exception ex)
                {
                    Log.Warn("CFG", "запись hv86.json: " + ex.Message);
                }
            }
        }

        /// <summary>Сброс кэша (тесты).</summary>
        public static void Invalidate()
        {
            lock (Gate) _cache = null;
        }

        public static Dictionary<string, object> View()
        {
            return Json.Obj(
                "text_font", TextFont,
                "text_font_size", TextFontSize,
                "capture_release", CaptureRelease,
                "replica_port", ReplicaPort,
                "replica_token", ReplicaToken,
                "witness_url", WitnessUrl,
                "witness_token", WitnessToken,
                "capture_on_click", CaptureOnClick,
                "fonts", Fonts());
        }

        public static Dictionary<string, object> Apply(Dictionary<string, object> parameters)
        {
            if (parameters != null)
            {
                if (parameters.ContainsKey("text_font")) TextFont = Json.GetString(parameters, "text_font", "");

                if (parameters.ContainsKey("text_font_size"))
                    TextFontSize = Json.GetInt(parameters, "text_font_size", 0);

                if (parameters.ContainsKey("capture_release"))
                    CaptureRelease = Json.GetString(parameters, "capture_release", "f8f12");

                if (parameters.ContainsKey("replica_port"))
                    ReplicaPort = Json.GetInt(parameters, "replica_port", 0);

                if (parameters.ContainsKey("replica_token"))
                    ReplicaToken = Json.GetString(parameters, "replica_token", "");

                if (parameters.ContainsKey("witness_url"))
                    WitnessUrl = Json.GetString(parameters, "witness_url", "");

                if (parameters.ContainsKey("witness_token"))
                    WitnessToken = Json.GetString(parameters, "witness_token", "");

                if (parameters.ContainsKey("capture_on_click"))
                    CaptureOnClick = Json.GetBool(parameters, "capture_on_click", true);
            }

            return View();
        }
    }
}