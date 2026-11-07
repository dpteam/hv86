using System;
using System.IO;
using System.Text;

namespace Hv86.Core
{
    /// <summary>Доступ к вшитым ресурсам: UI и v86.</summary>
    public static class Resources
    {
        // UI
        public const string ManagerHtml = "Hv86.Web.manager.html";
        public const string ManagerJs = "Hv86.Web.manager.js";
        public const string ConsoleHtml = "Hv86.Web.console.html";
        public const string ConsoleJs = "Hv86.Web.console.js";
        public const string ThemeCss = "Hv86.Web.theme.css";
        public const string I18nRu = "Hv86.Web.i18n.ru.json";
        public const string I18nEn = "Hv86.Web.i18n.en.json";

        // v86 (см. assets\v86\manifest.json)
        public const string V86Lib = "Hv86.Assets.v86.libv86.js";
        public const string V86Wasm = "Hv86.Assets.v86.v86.wasm";
        public const string V86Seabios = "Hv86.Assets.v86.seabios.bin";
        public const string V86Vgabios = "Hv86.Assets.v86.vgabios.bin";

        /// <summary>Манифест вшитых ассетов v86: версия, размеры и SHA-256.</summary>
        public const string V86Manifest = "Hv86.Assets.v86.manifest.json";

        // Каталог пресетов
        public const string Presets = "Hv86.Assets.presets.json";

        /// <summary>Каталог образов с copy.sh/v86 (генерируется tools\import-copy-sh.ps1).</summary>
        public const string PresetsCopy = "Hv86.Assets.presets-copy.json";

        /// <summary>Отобранные образы с old-dos.ru (tools\old-dos-select.mjs).</summary>
        public const string PresetsOldDos = "Hv86.Assets.presets-olddos.json";

        /// <summary>Читает текстовый ресурс. Нет ресурса - пустая строка, не null.</summary>
        public static string Text(string logicalName)
        {
            if (string.IsNullOrEmpty(logicalName)) return string.Empty;
            try
            {
                using (Stream stream = typeof(Resources).Assembly.GetManifestResourceStream(logicalName))
                {
                    if (stream == null) return string.Empty;
                    using (StreamReader reader = new StreamReader(stream, Encoding.UTF8, true))
                        return reader.ReadToEnd();
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>Читает бинарный ресурс. Нет ресурса - null.</summary>
        public static byte[] Bytes(string logicalName)
        {
            if (string.IsNullOrEmpty(logicalName)) return null;
            try
            {
                using (Stream stream = typeof(Resources).Assembly.GetManifestResourceStream(logicalName))
                {
                    if (stream == null) return null;
                    using (MemoryStream memory = new MemoryStream())
                    {
                        stream.CopyTo(memory);
                        return memory.ToArray();
                    }
                }
            }
            catch
            {
                return null;
            }
        }

        public static long Size(string logicalName)
        {
            try
            {
                using (Stream stream = typeof(Resources).Assembly.GetManifestResourceStream(logicalName))
                    return stream == null ? -1 : stream.Length;
            }
            catch
            {
                return -1;
            }
        }

        public static bool Exists(string logicalName)
        {
            try { return typeof(Resources).Assembly.GetManifestResourceInfo(logicalName) != null; }
            catch { return false; }
        }

        public static string[] Names()
        {
            try
            {
                string[] names = typeof(Resources).Assembly.GetManifestResourceNames();
                Array.Sort(names, StringComparer.Ordinal);
                return names;
            }
            catch
            {
                return new string[0];
            }
        }
    }
}