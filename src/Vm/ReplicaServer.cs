using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>
    /// Приёмник реплик: HTTP-эндпоинт `/replica/v1/...` (протокол - в
    /// docs/replica-remote.md).
    ///
    /// Это только хранилище: по сети нельзя ни запустить, ни остановить ВМ.
    /// Промоушен - локальная команда службы на самой реплике, иначе один хост
    /// мог бы управлять другим без всякого fencing'а.
    ///
    /// Токен сравнивается побайтово без раннего выхода: иначе по времени ответа
    /// можно подбирать секрет по одному символу.
    /// </summary>
    public sealed class ReplicaServer : IDisposable
    {
        public const string PathPrefix = "/replica/v1/";

        private readonly ReplicaStore _store;
        private readonly string _token;
        private readonly int _port;
        private readonly bool _preferAll;
        private readonly Func<string, string, bool> _leaseCheck;

        private HttpListener _listener;
        private CancellationTokenSource _cancel;
        private string _hostPrefix;

        public ReplicaServer(ReplicaStore store, int port, string token, bool bindAll = true,
            Func<string, string, bool> leaseCheck = null)
        {
            if (store == null) throw new Hv86Exception("хранилище реплик не задано");
            if (port <= 0 || port > 65535) throw new Hv86Exception("некорректный порт: " + port);

            _store = store;
            _token = token ?? "";
            _port = port;
            _preferAll = bindAll;
            _leaseCheck = leaseCheck;

            _hostPrefix = bindAll ? "+" : "localhost";
        }

        public int Port { get { return _port; } }

        /// <summary>true - слушаем все интерфейсы, false - только эта машина.</summary>
        public bool BoundAll
        {
            get { return _hostPrefix == "+"; }
        }

        /// <summary>
        /// Адрес, по которому приёмник реально отвечает. Клиенту нужно ходить
        /// именно сюда: у `+` локальный вход - 127.0.0.1, у `localhost` - localhost.
        /// </summary>
        public string BaseUrl
        {
            get
            {
                string host = _hostPrefix == "+" ? "127.0.0.1" : _hostPrefix;

                return "http://" + host + ":" + _port + PathPrefix;
            }
        }

        public string Url
        {
            get { return "http://" + (_hostPrefix == "+" ? "127.0.0.1" : _hostPrefix) + ":" + _port + PathPrefix; }
        }

        public void Start()
        {
            // Порядок попыток: сначала то, что просили, потом запасные варианты.
            // На разных машинах (и в CI) http.sys разрешает разное: `+` требует
            // прав/urlacl, `localhost` иногда не поднимается без IPv6, а
            // 127.0.0.1 работает почти всегда. Молча не сдаёмся - пишем в лог,
            // какой вариант получился.
            string[] attempts = _preferAll
                ? new[] { "+", "localhost", "127.0.0.1" }
                : new[] { "localhost", "127.0.0.1" };

            Exception last = null;

            foreach (string host in attempts)
            {
                try
                {
                    Open(host);
                    _hostPrefix = host;
                    last = null;
                    break;
                }
                catch (HttpListenerException ex)
                {
                    last = ex;
                    Log.Warn("REPL", "порт " + _port + " на \"" + host + "\": " + ex.Message);
                }
            }

            if (last != null)
                throw new Hv86Exception("не удалось открыть порт реплики " + _port + ": " + last.Message);

            if (!_preferAll && _hostPrefix != "localhost")
                Log.Warn("REPL", "localhost недоступен, слушаю " + _hostPrefix + " (только эта машина)");
            else if (_preferAll && !BoundAll)
                Log.Warn("REPL", "все интерфейсы недоступны, слушаю " + _hostPrefix + " (только эта машина)");

            _cancel = new CancellationTokenSource();

            Task.Run(() => AcceptLoopAsync(_cancel.Token));

            Log.Info("REPL", "приём реплик на порту " + _port + " (" + _hostPrefix + ")");
        }

        private void Open(string hostPrefix)
        {
            HttpListener listener = new HttpListener();
            listener.Prefixes.Add("http://" + hostPrefix + ":" + _port + PathPrefix);

            listener.Start();
            _listener = listener;
        }

        public void Stop()
        {
            try { _cancel?.Cancel(); } catch { }
            try { _listener?.Stop(); } catch { }
            try { _listener?.Close(); } catch { }

            _listener = null;
        }

        public void Dispose()
        {
            Stop();
            _cancel?.Dispose();
            _cancel = null;
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                HttpListenerContext context;

                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested || !(ex is HttpListenerException || ex is ObjectDisposedException))
                        Log.Warn("REPL", "приём: " + ex.Message);
                    else
                        Log.Warn("REPL", "приём остановлен: " + ex.Message);

                    return;
                }

                // каждый запрос - отдельно: большой state не должен блокировать hello
                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            string method = context.Request.HttpMethod;

            try
            {
                if (!TokenOk(context.Request))
                {
                    await RespondAsync(context, 401, Json.Obj("error", "bad token")).ConfigureAwait(false);
                    return;
                }

                string path = context.Request.Url == null ? "" : context.Request.Url.AbsolutePath;

                if (path.EndsWith("/hello", StringComparison.OrdinalIgnoreCase) && method == "GET")
                {
                    await RespondAsync(context, 200, Json.Obj(
                        "product", AppInfo.Name,
                        "version", AppInfo.Version,
                        "v86", AppInfo.V86Version,
                        "max_mb", 4096)).ConfigureAwait(false);
                    return;
                }

                if (path.EndsWith("/begin", StringComparison.OrdinalIgnoreCase) && method == "POST")
                {
                    Dictionary<string, string> query = Query(context.Request.Url);

                    string vmId = Value(query, "vm");
                    int epoch = ToInt(Value(query, "epoch"));
                    long size = ToLong(Value(query, "disk_size"));

                    Dictionary<string, object> meta = _store.Begin(vmId, epoch,
                        Value(query, "name"), Value(query, "vm_json"), size,
                        Value(query, "disk_sha256"), context.Request.InputStream);

                    await RespondAsync(context, 200, meta).ConfigureAwait(false);
                    return;
                }

                if (path.EndsWith("/state", StringComparison.OrdinalIgnoreCase) && method == "PUT")
                {
                    Dictionary<string, string> query = Query(context.Request.Url);

                    // аренда: state от бывшего основного, чья аренда уже
                    // передана, не должен затирать реплику (docs/replica-quorum.md)
                    if (_leaseCheck != null)
                    {
                        string lease = Value(query, "lease");

                        if (!_leaseCheck(Value(query, "vm"), lease))
                        {
                            await RespondAsync(context, 409, Json.Obj(
                                "error", "аренда недействительна: состояние принято не будет")).ConfigureAwait(false);
                            return;
                        }
                    }

                    bool duplicate;
                    Dictionary<string, object> meta = _store.PutState(
                        Value(query, "vm"),
                        ToInt(Value(query, "epoch")),
                        ToLong(Value(query, "seq")),
                        context.Request.InputStream, out duplicate,
                        ToInt(Value(query, "relay_port")));

                    await RespondAsync(context, 200, Json.Obj(
                        "ok", true,
                        "duplicate", duplicate,
                        "seq", Json.GetLong(meta, "seq", 0))).ConfigureAwait(false);
                    return;
                }

                if (path.EndsWith("/media", StringComparison.OrdinalIgnoreCase) && method == "PUT")
                {
                    Dictionary<string, string> query = Query(context.Request.Url);

                    Dictionary<string, object> meta = _store.PutMedia(
                        Value(query, "vm"),
                        ToInt(Value(query, "epoch")),
                        Value(query, "path"),
                        ToLong(Value(query, "size")),
                        Value(query, "sha256"),
                        context.Request.InputStream);

                    await RespondAsync(context, 200, Json.Obj("ok", true,
                        "media", Json.GetList(meta, "media") == null ? 0 : Json.GetList(meta, "media").Count))
                        .ConfigureAwait(false);
                    return;
                }

                if (path.EndsWith("/status", StringComparison.OrdinalIgnoreCase) && method == "GET")
                {
                    Dictionary<string, string> query = Query(context.Request.Url);

                    await RespondAsync(context, 200, _store.Status(Value(query, "vm"))).ConfigureAwait(false);
                    return;
                }

                if (path.EndsWith("/replica", StringComparison.OrdinalIgnoreCase) && method == "DELETE")
                {
                    Dictionary<string, string> query = Query(context.Request.Url);

                    _store.Delete(Value(query, "vm"));

                    await RespondAsync(context, 200, Json.Obj("ok", true)).ConfigureAwait(false);
                    return;
                }

                await RespondAsync(context, 404, Json.Obj("error", "unknown endpoint")).ConfigureAwait(false);
            }
            catch (ReplicaConflict ex)
            {
                await RespondAsync(context, 409, Json.Obj("error", ex.Message)).ConfigureAwait(false);
            }
            catch (Hv86Exception ex)
            {
                await RespondAsync(context, 400, Json.Obj("error", ex.Message)).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("REPL", "запрос " + method + ": " + ex.Message);
                await RespondAsync(context, 500, Json.Obj("error", ex.Message)).ConfigureAwait(false);
            }
        }

        private bool TokenOk(HttpListenerRequest request)
        {
            if (_token.Length == 0) return false;

            string given = request.Headers["X-Hv86-Token"] ?? "";

            byte[] a = Encoding.UTF8.GetBytes(_token);
            byte[] b = Encoding.UTF8.GetBytes(given);

            // разная длина - всё равно сравниваем все байты, чтобы не подсказывать
            int length = Math.Max(a.Length, b.Length);
            int diff = a.Length ^ b.Length;

            for (int i = 0; i < length; i++)
            {
                byte x = i < a.Length ? a[i] : (byte)0;
                byte y = i < b.Length ? b[i] : (byte)0;

                diff |= x ^ y;
            }

            return diff == 0;
        }

        private static async Task RespondAsync(HttpListenerContext context, int code, Dictionary<string, object> body)
        {
            try
            {
                byte[] bytes = Encoding.UTF8.GetBytes(Json.Stringify(body));

                context.Response.StatusCode = code;
                context.Response.ContentType = "application/json; charset=utf-8";
                context.Response.ContentLength64 = bytes.Length;
                context.Response.Headers["X-Hv86-Replica"] = AppInfo.V86Version;

                if (bytes.Length > 0) await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length)
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("REPL", "ответ: " + ex.Message);
            }
            finally
            {
                try { context.Response.Close(); } catch { }
            }
        }

        private static Dictionary<string, string> Query(Uri url)
        {
            Dictionary<string, string> result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            if (url == null || string.IsNullOrEmpty(url.Query)) return result;

            foreach (string item in url.Query.TrimStart('?').Split('&'))
            {
                if (item.Length == 0) continue;

                int split = item.IndexOf('=');

                if (split < 0) result[Uri.UnescapeDataString(item)] = "";
                else result[Uri.UnescapeDataString(item.Substring(0, split))] =
                    Uri.UnescapeDataString(item.Substring(split + 1));
            }

            return result;
        }

        private static string Value(Dictionary<string, string> query, string key)
        {
            string value;

            return query.TryGetValue(key, out value) ? value : "";
        }

        private static int ToInt(string text)
        {
            int value;
            return int.TryParse(text, out value) ? value : 0;
        }

        private static long ToLong(string text)
        {
            long value;
            return long.TryParse(text, out value) ? value : 0;
        }
    }
}