using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>
    /// Арбитр «кто главный»: выдаёт lease одному владельцу на время.
    ///
    /// Без свидетеля двух хостов нельзя пускать в автомат: при разделе сети оба
    /// решат, что второй умер. Здесь решение принимает один процесс, и только
    /// он считает время - часы клиентов в этом не участвуют.
    ///
    /// Протокол и границы - docs/replica-quorum.md.
    /// </summary>
    public sealed class WitnessStore
    {
        private readonly string _file;
        private readonly object _gate = new object();

        public WitnessStore(string root)
        {
            System.IO.Directory.CreateDirectory(root);
            _file = Path.Combine(root, "leases.json");
        }

        public static WitnessStore Default()
        {
            return new WitnessStore(Path.Combine(Paths.Root, "witness"));
        }

        /// <summary>Взять аренду. Выдаётся только если прежняя истекла.</summary>
        public Dictionary<string, object> Claim(string vmId, string holder, int ttlSec)
        {
            if (!ReplicaStore.IsSafeId(vmId)) throw new Hv86Exception("некорректный id ВМ: " + vmId);

            ttlSec = Clamp(ttlSec);

            lock (_gate)
            {
                Dictionary<string, object> leases = Load();

                Dictionary<string, object> current = Entry(leases, vmId);
                DateTime now = DateTime.UtcNow;

                if (current != null && ParseUtc(current, "expires_utc") > now)
                {
                    return Json.Obj(
                        "granted", false,
                        "holder", Json.GetString(current, "holder", ""),
                        "expires_in_sec", (long)Math.Ceiling((ParseUtc(current, "expires_utc") - now).TotalSeconds));
                }

                string token = Guid.NewGuid().ToString("N");

                leases[vmId] = Json.Obj(
                    "holder", holder ?? "",
                    "token_sha256", Hash(token),
                    "expires_utc", now.AddSeconds(ttlSec).ToString("o"),
                    "granted_utc", now.ToString("o"));

                Save(leases);

                Log.Info("WIT", "аренда " + vmId + " выдана " + holder);

                return Json.Obj("granted", true, "token", token, "ttl_sec", ttlSec);
            }
        }

        public Dictionary<string, object> Renew(string vmId, string token, int ttlSec)
        {
            ttlSec = Clamp(ttlSec);

            lock (_gate)
            {
                Dictionary<string, object> leases = Load();
                Dictionary<string, object> current = Entry(leases, vmId);

                if (current == null || !Matches(current, token))
                    throw new ReplicaConflict("аренда не ваша: " + vmId);

                DateTime now = DateTime.UtcNow;

                if (ParseUtc(current, "expires_utc") <= now)
                    throw new ReplicaConflict("аренда истекла: " + vmId);

                current["expires_utc"] = now.AddSeconds(ttlSec).ToString("o");
                Save(leases);

                return Json.Obj("ok", true, "ttl_sec", ttlSec);
            }
        }

        public Dictionary<string, object> Release(string vmId, string token)
        {
            lock (_gate)
            {
                Dictionary<string, object> leases = Load();
                Dictionary<string, object> current = Entry(leases, vmId);

                if (current == null) return Json.Obj("ok", true);

                if (!Matches(current, token)) throw new ReplicaConflict("аренда не ваша: " + vmId);

                leases.Remove(vmId);
                Save(leases);

                Log.Info("WIT", "аренда " + vmId + " отпущена");

                return Json.Obj("ok", true);
            }
        }

        public Dictionary<string, object> Status(string vmId)
        {
            lock (_gate)
            {
                Dictionary<string, object> leases = Load();
                Dictionary<string, object> current = Entry(leases, vmId);

                if (current == null) return Json.Obj("vm_id", vmId, "holder", "", "expires_in_sec", 0, "active", false);

                DateTime expires = ParseUtc(current, "expires_utc");
                DateTime now = DateTime.UtcNow;

                return Json.Obj(
                    "vm_id", vmId,
                    "holder", Json.GetString(current, "holder", ""),
                    "expires_in_sec", (long)Math.Max(0, Math.Ceiling((expires - now).TotalSeconds)),
                    "active", expires > now,
                    "token_fingerprint", Short(Json.GetString(current, "token_sha256", "")));
            }
        }

        /// <summary>Действителен ли токен для этой ВМ прямо сейчас.</summary>
        public bool IsCurrent(string vmId, string token)
        {
            if (string.IsNullOrEmpty(token)) return false;

            lock (_gate)
            {
                Dictionary<string, object> current = Entry(Load(), vmId);

                return current != null &&
                       ParseUtc(current, "expires_utc") > DateTime.UtcNow &&
                       Matches(current, token);
            }
        }

        // ------------------------------------------------------------ служебное

        private static int Clamp(int ttlSec)
        {
            return Math.Max(2, Math.Min(600, ttlSec <= 0 ? 12 : ttlSec));
        }

        private static Dictionary<string, object> Entry(Dictionary<string, object> leases, string vmId)
        {
            object value;

            return leases.TryGetValue(vmId, out value) ? value as Dictionary<string, object> : null;
        }

        private static bool Matches(Dictionary<string, object> entry, string token)
        {
            string expected = Json.GetString(entry, "token_sha256", "");
            string actual = Hash(token ?? "");

            return expected.Length > 0 && expected == actual;
        }

        private static DateTime ParseUtc(Dictionary<string, object> entry, string key)
        {
            DateTime value;

            return DateTime.TryParse(Json.GetString(entry, key, ""), out value)
                ? value.ToUniversalTime()
                : DateTime.MinValue;
        }

        private static string Hash(string text)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(text));
                StringBuilder builder = new StringBuilder(hash.Length * 2);

                foreach (byte b in hash) builder.Append(b.ToString("x2"));

                return builder.ToString();
            }
        }

        private static string Short(string hash)
        {
            return string.IsNullOrEmpty(hash) ? "" : hash.Substring(0, Math.Min(12, hash.Length));
        }

        private Dictionary<string, object> Load()
        {
            Dictionary<string, object> doc = Json.ReadFile(_file);

            return doc ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        }

        private void Save(Dictionary<string, object> leases)
        {
            Json.WriteFile(_file, leases);
        }
    }

    /// <summary>
    /// HTTP-обёртка над <see cref="WitnessStore"/>: та же схема, что у приёмника
    /// реплик (токен, JSON, без TLS).
    /// </summary>
    public sealed class WitnessServer : IDisposable
    {
        public const string PathPrefix = "/witness/v1/";

        private readonly WitnessStore _store;
        private readonly string _token;
        private readonly int _port;
        private readonly int _ttlSec;
        private readonly bool _preferAll;

        private HttpListener _listener;
        private CancellationTokenSource _cancel;
        private string _hostPrefix;

        public WitnessServer(WitnessStore store, int port, string token, int ttlSec = 12, bool bindAll = true)
        {
            if (store == null) throw new Hv86Exception("хранилище аренд не задано");
            if (port <= 0 || port > 65535) throw new Hv86Exception("некорректный порт: " + port);

            _store = store;
            _token = token ?? "";
            _port = port;
            _ttlSec = ttlSec;
            _preferAll = bindAll;
            _hostPrefix = bindAll ? "+" : "localhost";
        }

        public string BaseUrl { get { return "http://" + (_hostPrefix == "+" ? "127.0.0.1" : _hostPrefix) +
                                              ":" + _port + PathPrefix; } }

        public void Start()
        {
            if (_token.Length == 0) throw new Hv86Exception("свидетель без токена не принимает запросы");

            string[] attempts = _preferAll ? new[] { "+", "localhost", "127.0.0.1" }
                                           : new[] { "localhost", "127.0.0.1" };

            Exception last = null;

            foreach (string host in attempts)
            {
                try
                {
                    HttpListener listener = new HttpListener();
                    listener.Prefixes.Add("http://" + host + ":" + _port + PathPrefix);
                    listener.Start();

                    _listener = listener;
                    _hostPrefix = host;
                    last = null;
                    break;
                }
                catch (HttpListenerException ex)
                {
                    last = ex;
                }
            }

            if (last != null)
                throw new Hv86Exception("не удалось открыть порт свидетеля " + _port + ": " + last.Message);

            _cancel = new CancellationTokenSource();
            Task.Run(() => AcceptLoopAsync(_cancel.Token));

            Log.Info("WIT", "свидетель на порту " + _port + " (" + _hostPrefix + "), TTL " + _ttlSec + " с");
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
                catch (Exception)
                {
                    return;
                }

                _ = Task.Run(() => HandleAsync(context));
            }
        }

        private async Task HandleAsync(HttpListenerContext context)
        {
            try
            {
                if (!TokenOk(context.Request))
                {
                    await RespondAsync(context, 401, Json.Obj("error", "bad token")).ConfigureAwait(false);
                    return;
                }

                string path = context.Request.Url == null ? "" : context.Request.Url.AbsolutePath;
                Dictionary<string, string> query = Query(context.Request.Url);

                string vm = Value(query, "vm");
                string token = Value(query, "token");
                int ttl = ToInt(Value(query, "ttl"));

                if (ttl <= 0) ttl = _ttlSec;

                if (path.EndsWith("/claim", StringComparison.OrdinalIgnoreCase))
                {
                    await RespondAsync(context, 200,
                        _store.Claim(vm, Value(query, "holder"), ttl)).ConfigureAwait(false);
                    return;
                }

                if (path.EndsWith("/renew", StringComparison.OrdinalIgnoreCase))
                {
                    await RespondAsync(context, 200, _store.Renew(vm, token, ttl)).ConfigureAwait(false);
                    return;
                }

                if (path.EndsWith("/release", StringComparison.OrdinalIgnoreCase))
                {
                    await RespondAsync(context, 200, _store.Release(vm, token)).ConfigureAwait(false);
                    return;
                }

                if (path.EndsWith("/status", StringComparison.OrdinalIgnoreCase))
                {
                    await RespondAsync(context, 200, _store.Status(vm)).ConfigureAwait(false);
                    return;
                }

                // проверка «этот токен ещё мой?» - для приёмника реплики
                if (path.EndsWith("/check", StringComparison.OrdinalIgnoreCase))
                {
                    await RespondAsync(context, 200,
                        Json.Obj("current", _store.IsCurrent(vm, token))).ConfigureAwait(false);
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
                await RespondAsync(context, 500, Json.Obj("error", ex.Message)).ConfigureAwait(false);
            }
        }

        private bool TokenOk(HttpListenerRequest request)
        {
            byte[] a = Encoding.UTF8.GetBytes(_token);
            byte[] b = Encoding.UTF8.GetBytes(request.Headers["X-Hv86-Token"] ?? "");

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

                if (bytes.Length > 0)
                    await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Log.Warn("WIT", "ответ: " + ex.Message);
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
    }

    /// <summary>Клиент свидетеля: взять/продлить/отпустить/спросить.</summary>
    public static class WitnessClient
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            HttpClient client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.ExpectContinue = false;

            return client;
        }

        public static string NormalizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) throw new Hv86Exception("не задан адрес свидетеля");

            string text = url.Trim().TrimEnd('/');

            if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                text = "http://" + text;

            if (text.IndexOf(WitnessServer.PathPrefix.Trim('/'), StringComparison.OrdinalIgnoreCase) < 0)
                text += WitnessServer.PathPrefix.TrimEnd('/');

            return text + "/";
        }

        public static Task<Dictionary<string, object>> ClaimAsync(string url, string secret,
            string vmId, string holder, int ttlSec, int timeoutMs)
        {
            return SendAsync("claim", url, secret,
                "?vm=" + Uri.EscapeDataString(vmId) +
                "&holder=" + Uri.EscapeDataString(holder) +
                "&ttl=" + ttlSec, timeoutMs);
        }

        public static Task<Dictionary<string, object>> RenewAsync(string url, string secret,
            string vmId, string token, int ttlSec, int timeoutMs)
        {
            return SendAsync("renew", url, secret,
                "?vm=" + Uri.EscapeDataString(vmId) +
                "&token=" + Uri.EscapeDataString(token) +
                "&ttl=" + ttlSec, timeoutMs);
        }

        public static Task<Dictionary<string, object>> ReleaseAsync(string url, string secret,
            string vmId, string token, int timeoutMs)
        {
            return SendAsync("release", url, secret,
                "?vm=" + Uri.EscapeDataString(vmId) +
                "&token=" + Uri.EscapeDataString(token), timeoutMs);
        }

        public static Task<Dictionary<string, object>> StatusAsync(string url, string secret,
            string vmId, int timeoutMs)
        {
            return SendAsync("status", url, secret, "?vm=" + Uri.EscapeDataString(vmId), timeoutMs);
        }

        /// <summary>Действителен ли токен аренды прямо сейчас (спрашивает приёмник реплики).</summary>
        public static Task<Dictionary<string, object>> CheckAsync(string url, string secret,
            string vmId, string token, int timeoutMs)
        {
            return SendAsync("check", url, secret,
                "?vm=" + Uri.EscapeDataString(vmId) +
                "&token=" + Uri.EscapeDataString(token ?? ""), timeoutMs);
        }

        private static async Task<Dictionary<string, object>> SendAsync(string method, string url,
            string secret, string query, int timeoutMs)
        {
            string full = NormalizeUrl(url) + method + query;

            using (HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, full))
            {
                request.Headers.TryAddWithoutValidation("X-Hv86-Token", secret ?? "");

                using (CancellationTokenSource cancel = new CancellationTokenSource(Math.Max(1000, timeoutMs)))
                {
                    HttpResponseMessage response;

                    try
                    {
                        response = await Http.SendAsync(request, cancel.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new Hv86Exception("свидетель не ответил за " + (timeoutMs / 1000) + " с");
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new Hv86Exception("свидетель недоступен: " + ex.Message);
                    }

                    using (response)
                    {
                        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Dictionary<string, object> reply = Json.ParseObjectSafe(text);

                        if (!response.IsSuccessStatusCode)
                            throw new Hv86Exception("свидетель ответил " + (int)response.StatusCode + ": " +
                                                    Json.GetString(reply, "error", text));

                        return reply ?? Json.Obj();
                    }
                }
            }
        }
    }
}