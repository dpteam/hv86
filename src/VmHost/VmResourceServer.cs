using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Hv86.Core;
using Microsoft.Web.WebView2.Core;

namespace Hv86.VmHost
{
    /// <summary>
    /// Отдаёт страницу консоли и всё, что нужно v86, по http://hv86.local/.
    ///
    /// Схема именно http, а не https: страница открывает WebSocket к
    /// локальному wisp-релею (ws://127.0.0.1:port), а Chromium запрещает
    /// небезопасный ws:// из https-документа (mixed content). Никаких других
    /// причин для https у нас нет - это локальная страница из вшитых ресурсов.
    ///
    /// Почему свой обработчик, а не SetVirtualHostNameToFolderMapping:
    /// образы дисков v86 читает кусками через Range-запросы, а ещё ему нужен
    /// точный Content-Length. Своим обработчиком мы это гарантируем, плюс
    /// ничего не распаковываем на диск и не даём странице доступ к файлам
    /// вне каталога ВМ.
    ///
    /// Маршруты:
    ///   /console.html            страница консоли (со вшитыми css/js)
    ///   /v86/libv86.js           эмулятор из ресурсов exe
    ///   /v86/v86.wasm
    ///   /v86/seabios.bin
    ///   /v86/vgabios.bin
    ///   /media/hda|cdrom|fda     образы текущей ВМ
    ///   /state/&lt;имя&gt;             state-файлы (снапшоты, чекпоинты)
    /// </summary>
    public sealed class VmResourceServer : IDisposable
    {
        public const string Origin = "http://hv86.local";

        private readonly CoreWebView2 _core;
        private readonly List<FileStream> _streams = new List<FileStream>();

        /// <summary>"hda"|"cdrom"|"fda" -> абсолютный путь (или null, если нет).</summary>
        public Func<string, string> MediaPath;

        /// <summary>Имя state-файла -> абсолютный путь (или null, если нельзя).</summary>
        public Func<string, string> StatePath;

        /// <summary>Готовая страница консоли (с вшитыми css/js).</summary>
        public string ConsoleHtml = "";

        public VmResourceServer(CoreWebView2 core)
        {
            _core = core;
            _core.WebResourceRequested += OnRequest;
            _core.AddWebResourceRequestedFilter(Origin + "/*", CoreWebView2WebResourceContext.All);
        }

        private void OnRequest(object sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            try
            {
                string uri = e.Request.Uri ?? "";
                if (!uri.StartsWith(Origin, StringComparison.OrdinalIgnoreCase))
                {
                    e.Response = Text("forbidden", 403, "Forbidden", "text/plain");
                    return;
                }

                string path = uri.Substring(Origin.Length);

                int query = path.IndexOf('?');
                if (query >= 0) path = path.Substring(0, query);

                if (path == "/" || path == "/console.html")
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(ConsoleHtml ?? "");
                    e.Response = BytesResponse(bytes, 200, "OK", "text/html; charset=utf-8");
                    return;
                }

                switch (path)
                {
                    case "/v86/libv86.js":
                        Embedded(e, Resources.V86Lib, "text/javascript; charset=utf-8");
                        return;

                    case "/v86/v86.wasm":
                        Embedded(e, Resources.V86Wasm, "application/wasm");
                        return;

                    case "/v86/seabios.bin":
                        Embedded(e, Resources.V86Seabios, "application/octet-stream");
                        return;

                    case "/v86/vgabios.bin":
                        Embedded(e, Resources.V86Vgabios, "application/octet-stream");
                        return;
                }

                if (path.StartsWith("/media/", StringComparison.OrdinalIgnoreCase))
                {
                    string slot = path.Substring("/media/".Length).ToLowerInvariant();
                    string file = MediaPath == null ? null : MediaPath(slot);

                    if (string.IsNullOrEmpty(file) || !File.Exists(file))
                    {
                        e.Response = Text("no media", 404, "Not Found", "text/plain");
                        return;
                    }

                    ServeFile(e, file, "application/octet-stream");
                    return;
                }

                if (path.StartsWith("/state/", StringComparison.OrdinalIgnoreCase))
                {
                    string name = path.Substring("/state/".Length);
                    string file = StatePath == null ? null : StatePath(name);

                    if (string.IsNullOrEmpty(file) || !File.Exists(file))
                    {
                        e.Response = Text("no state", 404, "Not Found", "text/plain");
                        return;
                    }

                    ServeFile(e, file, "application/octet-stream");
                    return;
                }

                e.Response = Text("not found", 404, "Not Found", "text/plain");
            }
            catch (Exception ex)
            {
                Log.Exception("RES", "запрос " + (e.Request == null ? "?" : e.Request.Uri), ex);

                try { e.Response = Text("error", 500, "Internal Error", "text/plain"); }
                catch { }
            }
        }

        // ------------------------------------------------------------ отдача

        private void Embedded(CoreWebView2WebResourceRequestedEventArgs e, string resourceName, string contentType)
        {
            byte[] bytes = Resources.Bytes(resourceName);

            if (bytes == null)
            {
                Log.Error("RES", "нет вшитого ресурса " + resourceName);
                e.Response = Text("missing resource", 500, "Internal Error", "text/plain");
                return;
            }

            e.Response = BytesResponse(bytes, 200, "OK", contentType);
        }

        private CoreWebView2WebResourceResponse BytesResponse(byte[] bytes, int status, string reason, string contentType)
        {
            MemoryStream stream = new MemoryStream(bytes, false);

            string headers =
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + bytes.Length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "Cache-Control: no-store\r\n" +
                "Access-Control-Allow-Origin: *\r\n";

            return _core.Environment.CreateWebResourceResponse(stream, status, reason, headers);
        }

        private CoreWebView2WebResourceResponse Text(string text, int status, string reason, string contentType)
        {
            return BytesResponse(Encoding.UTF8.GetBytes(text ?? ""), status, reason, contentType);
        }

        private void ServeFile(CoreWebView2WebResourceRequestedEventArgs e, string path, string contentType)
        {
            long size;

            try
            {
                size = new FileInfo(path).Length;
            }
            catch (Exception ex)
            {
                Log.Warn("RES", "файл " + path + ": " + ex.Message);
                e.Response = Text("cannot read", 500, "Internal Error", "text/plain");
                return;
            }

            string range = null;

            try { range = e.Request.Headers.GetHeader("Range"); }
            catch { }

            if (string.IsNullOrEmpty(range))
            {
                // Целиком: отдаём FileStream как есть, чтобы не тянуть 128 МБ
                // state-файла в память. Потоки живут до закрытия окна.
                FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.SequentialScan);

                lock (_streams) _streams.Add(stream);

                string headers =
                    "Content-Type: " + contentType + "\r\n" +
                    "Content-Length: " + size.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                    "Accept-Ranges: bytes\r\n" +
                    "Cache-Control: no-store\r\n" +
                    "Access-Control-Allow-Origin: *\r\n";

                e.Response = _core.Environment.CreateWebResourceResponse(stream, 200, "OK", headers);
                return;
            }

            long start = 0;
            long end = size - 1;

            if (!TryParseRange(range, size, out start, out end))
            {
                e.Response = Text("bad range", 416, "Range Not Satisfiable", "text/plain");
                return;
            }

            long length = end - start + 1;

            if (length > 64L * 1024 * 1024)
            {
                e.Response = Text("range too large", 416, "Range Not Satisfiable", "text/plain");
                return;
            }

            byte[] buffer = new byte[length];

            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                {
                    fs.Seek(start, SeekOrigin.Begin);

                    int read = 0;
                    while (read < length)
                    {
                        int chunk = fs.Read(buffer, read, (int)(length - read));
                        if (chunk <= 0) break;
                        read += chunk;
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("RES", "чтение куска " + path + ": " + ex.Message);
                e.Response = Text("cannot read", 500, "Internal Error", "text/plain");
                return;
            }

            string headers206 =
                "Content-Type: " + contentType + "\r\n" +
                "Content-Length: " + length.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "Content-Range: bytes " + start.ToString(CultureInfo.InvariantCulture) + "-" +
                    end.ToString(CultureInfo.InvariantCulture) + "/" + size.ToString(CultureInfo.InvariantCulture) + "\r\n" +
                "Accept-Ranges: bytes\r\n" +
                "Cache-Control: no-store\r\n" +
                "Access-Control-Allow-Origin: *\r\n";

            e.Response = _core.Environment.CreateWebResourceResponse(
                new MemoryStream(buffer, false), 206, "Partial Content", headers206);
        }

        private static bool TryParseRange(string header, long size, out long start, out long end)
        {
            start = 0;
            end = size - 1;

            if (string.IsNullOrEmpty(header)) return false;

            string value = header.Trim();

            int eq = value.IndexOf('=');
            if (eq >= 0) value = value.Substring(eq + 1).Trim();

            // многодиапазонные запросы v86 не делает: берём первый
            int comma = value.IndexOf(',');
            if (comma >= 0) value = value.Substring(0, comma).Trim();

            int dash = value.IndexOf('-');
            if (dash < 0) return false;

            string from = value.Substring(0, dash).Trim();
            string to = value.Substring(dash + 1).Trim();

            if (from.Length == 0)
            {
                // bytes=-N: последние N байт
                long tail;
                if (!long.TryParse(to, NumberStyles.Integer, CultureInfo.InvariantCulture, out tail) || tail <= 0)
                    return false;

                start = Math.Max(0, size - tail);
                end = size - 1;
                return true;
            }

            if (!long.TryParse(from, NumberStyles.Integer, CultureInfo.InvariantCulture, out start) || start < 0)
                return false;

            if (to.Length > 0)
            {
                if (!long.TryParse(to, NumberStyles.Integer, CultureInfo.InvariantCulture, out end)) return false;
            }
            else
            {
                end = size - 1;
            }

            if (end >= size) end = size - 1;
            return start <= end && start < size;
        }

        public void Dispose()
        {
            try { _core.WebResourceRequested -= OnRequest; } catch { }

            lock (_streams)
            {
                foreach (FileStream stream in _streams)
                    try { stream.Dispose(); } catch { }

                _streams.Clear();
            }
        }
    }
}