using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading.Tasks;
using Hv86.Core;
using Hv86.Vm;

namespace Hv86.VmHost
{
    /// <summary>
    /// Точка принятия решений по исходящему трафику для fetch-бэкенда v86
    /// (`net.mode=nat`).
    ///
    /// Зачем это здесь: в режиме nat сеть гостя обслуживает **браузерный fetch**
    /// внутри страницы консоли. Правила релея (`Vm/EgressPolicy`) к нему не
    /// относятся, поэтому до сих пор гость мог спокойно сходить на
    /// 127.0.0.1:порт хоста или в его LAN — тот же SSRF, только через браузер.
    ///
    /// Своей точки решения в странице быть не может: она не умеет разрешать
    /// имена, а если бы и умела — это был бы не контроль, а пожелание. Поэтому
    /// страница на каждый не-свой запрос делает синхронный POST сюда, а решение
    /// принимает C#: сначала резолвит имя, потом проверяет **адреса** той же
    /// политикой, что и релей (защита от DNS-rebinding).
    ///
    /// Ответ кэшируется на несколько секунд: гость с keep-alive иначе заставит
    /// нас резолвить одно и то же имя на каждый запрос.
    /// </summary>
    public sealed class EgressGate
    {
        /// <summary>Сколько живёт решение в кэше (секунды).</summary>
        private const int CacheSeconds = 30;

        /// <summary>Сколько ждём DNS, прежде чем отказать.</summary>
        private const int ResolveTimeoutMs = 3000;

        /// <summary>Когда кэш пора чистить (решение мелкое, но их много).</summary>
        private const int CacheLimit = 512;

        private sealed class Decision
        {
            public bool Allow;
            public string Reason;
            public string Host;
            public int Port;
            public string Addresses;
            public DateTime ExpiresUtc;
        }

        private readonly object _gate = new object();
        private readonly Dictionary<string, Decision> _cache =
            new Dictionary<string, Decision>(StringComparer.OrdinalIgnoreCase);

        private EgressPolicy _policy;

        public EgressGate(EgressPolicy policy)
        {
            _policy = policy ?? new EgressPolicy();
        }

        public EgressPolicy Policy
        {
            get { lock (_gate) return _policy; }
        }

        /// <summary>Новая политика: кэш решений больше не годится.</summary>
        public void SetPolicy(EgressPolicy policy)
        {
            lock (_gate)
            {
                _policy = policy ?? new EgressPolicy();
                _cache.Clear();
            }

            Log.Info("NET", "egress (fetch): " + _policy.Describe());
        }

        /// <summary>
        /// Решение по URL. Возвращает словарь для JSON:
        /// {allow, reason, host, port, addresses}.
        /// </summary>
        public Dictionary<string, object> Decide(string url)
        {
            Uri uri;

            if (string.IsNullOrEmpty(url) || !Uri.TryCreate(url, UriKind.Absolute, out uri))
                return View(false, "bad url", "", 0, "");

            string scheme = uri.Scheme.ToLowerInvariant();

            // не http(s): v86 этим не ходит, а data:/blob: не наш случай
            if (scheme != "http" && scheme != "https")
                return View(true, "not http", "", 0, "");

            EgressPolicy policy = Policy;

            if (policy.IsUnrestricted)
                return View(true, "any", uri.DnsSafeHost, uri.Port, "");

            string host = uri.DnsSafeHost;
            int port = uri.Port;

            if (string.IsNullOrEmpty(host)) return View(false, "no host", "", port, "");

            string key = host + ":" + port.ToString(CultureInfo.InvariantCulture);

            lock (_gate)
            {
                Decision cached;

                if (_cache.TryGetValue(key, out cached) && cached.ExpiresUtc > DateTime.UtcNow)
                    return View(cached.Allow, cached.Reason, cached.Host, cached.Port, cached.Addresses);

                if (_cache.Count > CacheLimit) _cache.Clear();
            }

            IPAddress[] addresses;
            string error;

            if (!Resolve(host, out addresses, out error))
            {
                Log.Warn("NET", "egress (fetch): " + host + " не разрешается: " + error);
                Remember(key, false, error, host, port, "");
                return View(false, error, host, port, "");
            }

            bool allowed = policy.IsAllowed(host, addresses, port);

            string list = Describe(addresses);
            string reason = allowed ? (policy.Mode == EgressPolicy.ModeSafe ? "safe: public" : "allowlist: match") : DenyReason(policy);

            Remember(key, allowed, reason, host, port, list);

            if (!allowed)
                Log.Warn("NET", "egress (fetch): запрещено " + host + ":" + port + " -> " + list +
                                 " (" + reason + ")");

            return View(allowed, reason, host, port, list);
        }

        // ------------------------------------------------------------ утилиты

        private static string DenyReason(EgressPolicy policy)
        {
            return policy.Mode == EgressPolicy.ModeSafe ? "loopback/private address" : "not in allowlist";
        }

        private static bool Resolve(string host, out IPAddress[] addresses, out string error)
        {
            addresses = null;
            error = "";

            IPAddress single;

            if (IPAddress.TryParse(host, out single))
            {
                addresses = new[] { single };
                return true;
            }

            try
            {
                Task<IPAddress[]> lookup = Dns.GetHostAddressesAsync(host);

                if (!lookup.Wait(ResolveTimeoutMs))
                {
                    error = "dns timeout";
                    return false;
                }

                addresses = lookup.Result;
            }
            catch (Exception ex)
            {
                error = ex is AggregateException && ex.InnerException != null
                    ? "dns failed: " + ex.InnerException.Message
                    : "dns failed: " + ex.Message;

                return false;
            }

            if (addresses == null || addresses.Length == 0)
            {
                error = "dns empty";
                return false;
            }

            return true;
        }

        private static string Describe(IPAddress[] addresses)
        {
            if (addresses == null || addresses.Length == 0) return "";

            System.Text.StringBuilder sb = new System.Text.StringBuilder();

            foreach (IPAddress address in addresses)
            {
                if (sb.Length > 0) sb.Append(", ");
                sb.Append(address.ToString());

                if (address.AddressFamily == AddressFamily.InterNetworkV6) sb.Append(" [v6]");
            }

            return sb.ToString();
        }

        private void Remember(string key, bool allow, string reason, string host, int port, string addresses)
        {
            // отказ по DNS не кэшируем надолго: имя может появиться
            bool negativeDns = !allow && (reason == "dns timeout" || reason.StartsWith("dns ", StringComparison.Ordinal));

            lock (_gate)
            {
                _cache[key] = new Decision
                {
                    Allow = allow,
                    Reason = reason,
                    Host = host,
                    Port = port,
                    Addresses = addresses,
                    ExpiresUtc = DateTime.UtcNow.AddSeconds(negativeDns ? 5 : CacheSeconds)
                };
            }
        }

        private static Dictionary<string, object> View(bool allow, string reason, string host, int port, string addresses)
        {
            return Json.Obj(
                "allow", allow,
                "reason", reason,
                "host", host ?? "",
                "port", port,
                "addresses", addresses ?? "");
        }
    }
}