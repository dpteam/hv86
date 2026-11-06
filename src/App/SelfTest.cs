using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Hv86.Core;
using Hv86.Vm;
using Microsoft.Web.WebView2.Core;

namespace Hv86.App
{
    /// <summary>
    /// `hv86.exe --selftest`: проверяет, что сборка вообще способна работать.
    ///
    /// Проверяем не «красиво ли», а то, без чего гипервизор не запустится:
    /// вшитые ресурсы, рантайм WebView2, запись в каталог данных, каталог
    /// пресетов и состояние службы. Код возврата 0/1 - для CI.
    /// </summary>
    public static class SelfTest
    {
        public static int Run()
        {
            int failures = 0;
            StringBuilder report = new StringBuilder();

            void Line(string text)
            {
                Console.WriteLine(text);
                report.AppendLine(text);
            }

            void Check(bool ok, string what, string detail)
            {
                if (!ok) failures++;
                Line((ok ? "[ok]   " : "[FAIL] ") + what + (string.IsNullOrEmpty(detail) ? "" : " - " + detail));
            }

            Line(AppInfo.Summary());
            Line("data: " + Paths.Root + (Paths.IsPortable ? " (portable)" : ""));
            Line("");

            // ---- ресурсы
            string[] web =
            {
                Resources.ManagerHtml, Resources.ManagerJs, Resources.ConsoleHtml,
                Resources.ConsoleJs, Resources.ThemeCss, Resources.I18nRu, Resources.I18nEn
            };

            foreach (string name in web)
                Check(Resources.Exists(name), "resource " + name,
                    Resources.Size(name) + " bytes");

            string[] v86 =
            {
                Resources.V86Lib, Resources.V86Wasm, Resources.V86Seabios, Resources.V86Vgabios
            };

            long v86Total = 0;

            foreach (string name in v86)
            {
                long size = Resources.Size(name);
                v86Total += Math.Max(0, size);
                Check(Resources.Exists(name) && size > 0, "v86 " + name, size + " bytes");
            }

            Check(v86Total > 2 * 1024 * 1024, "v86 assets total", v86Total + " bytes");

            // ---- пресеты
            List<Dictionary<string, object>> presets = PresetCatalog.All();
            Check(presets.Count > 0, "preset catalog", presets.Count + " entries");

            foreach (Dictionary<string, object> preset in presets)
            {
                string id = Json.GetString(preset, "id", "");
                string hash = Json.GetString(preset, "sha256", "");
                bool copy = Json.GetString(preset, "source", "") == "copy.sh";

                // у каталога copy.sh хэшей нет: они закрепляются при первой
                // загрузке (PresetCatalog.PinHash), у своих - обязательны
                bool ok = id.Length > 0 && (copy ? hash.Length == 0 || hash.Length == 64 : hash.Length == 64);

                Check(ok, "preset " + id,
                    copy ? "copy.sh, hash pinned on first download" : (hash.Length == 64 ? "sha256 ok" : "no sha256"));
            }

            // ---- WebView2 runtime
            try
            {
                string version = CoreWebView2Environment.GetAvailableBrowserVersionString();
                Check(!string.IsNullOrEmpty(version), "WebView2 runtime", version);
            }
            catch (Exception ex)
            {
                Check(false, "WebView2 runtime", ex.Message);
            }

            // ---- каталог данных
            try
            {
                Paths.EnsureDirectories();
                string probe = Path.Combine(Paths.Root, ".selftest-" + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "hv86");
                File.Delete(probe);
                Check(true, "data dir is writable", Paths.Root);
            }
            catch (Exception ex)
            {
                Check(false, "data dir is writable", ex.Message);
            }

            // ---- служба
            ServiceStatus status = ServiceManager.Query();
            Line("");
            Line("service: " + status.Text() + (status.Path.Length > 0 ? " [" + status.Path + "]" : ""));
            if (!status.Installed) Line("         (install with: hv86.exe --service install)");
            if (status.Problem.Length > 0) Line("         " + status.Problem);

            // ---- среда запуска
            Line("");
            Line("session: " + System.Diagnostics.Process.GetCurrentProcess().SessionId +
                 ", active console session: " + SessionLauncher.ActiveConsoleSessionId);
            Line("user: " + Elevation.CurrentUser + ", elevated: " + Elevation.IsElevated);

            Line("");
            Line(failures == 0 ? "SELFTEST: OK" : "SELFTEST: FAILED (" + failures + ")");

            try
            {
                Paths.EnsureDirectories();
                File.WriteAllText(Path.Combine(Paths.Root, "selftest.txt"), report.ToString(), new UTF8Encoding(false));
            }
            catch { }

            return failures == 0 ? 0 : 1;
        }
    }
}