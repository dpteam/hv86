using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>
    /// Правила исходящего трафика для wisp-релея (режим net.mode=relay).
    ///
    /// Зачем: релей открывает TCP от имени гостя, и без правил гость может
    /// ходить в localhost хоста, в его LAN и к админским портам — это SSRF
    /// в чистом виде. Политика проверяется по **разрешённым адресам**, а не
    /// по строке имени, иначе DNS-rebinding обходит любые списки.
    ///
    /// Режимы:
    ///   any       - без ограничений (по умолчанию, поведение до правил);
    ///   safe      - только интернет: localhost, приватные сети и link-local
    ///               запрещены (типовой случай «гость в интернет, но не в мой LAN»);
    ///   allowlist - только перечисленные цели, всё остальное запрещено.
    ///
    /// Запись списка: `host`, `host:port`, `1.2.3.4`, `10.0.0.0/8`,
    /// `10.0.0.0/8:22`, `*` (любой хост), `*:443`.
    /// </summary>
    public sealed class EgressPolicy
    {
        public const string ModeAny = "any";
        public const string ModeSafe = "safe";
        public const string ModeAllowList = "allowlist";

        public const int MaxRules = 64;
        public const int MaxRuleLength = 128;

        public string Mode = ModeAny;
        public List<EgressRule> Allow = new List<EgressRule>();

        public bool IsAllowList { get { return Mode == ModeAllowList; } }
        public bool IsUnrestricted { get { return Mode == ModeAny; } }

        /// <summary>
        /// Можно ли гостю соединиться с host:port, если это имя разрешилось
        /// в addresses. Проверяются все адреса: если хоть один запрещён -
        /// соединение не открываем (защита от «IP из двух сетей»).
        /// </summary>
        public bool IsAllowed(string host, IList<IPAddress> addresses, int port)
        {
            if (Mode == ModeAny) return true;

            if (addresses == null || addresses.Count == 0) return false;

            if (Mode == ModeSafe)
            {
                foreach (IPAddress address in addresses)
                    if (IsPrivate(address)) return false;

                return true;
            }

            if (Mode == ModeAllowList)
            {
                if (Allow == null || Allow.Count == 0) return false;

                foreach (EgressRule rule in Allow)
                    foreach (IPAddress address in addresses)
                        if (rule.Matches(host, address, port)) return true;

                return false;
            }

            return false;
        }

        /// <summary>Приватные/локальные адреса, которые в режиме safe запрещены.</summary>
        public static bool IsPrivate(IPAddress address)
        {
            if (address == null) return true;

            if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast) return true;
                if (IPAddress.IsLoopback(address)) return true;

                byte[] bytes = address.GetAddressBytes();

                // ULA fc00::/7
                if ((bytes[0] & 0xFE) == 0xFC) return true;

                // IPv4-mapped: проверяем вложенный адрес по IPv4-правилам
                if (address.IsIPv4MappedToIPv6) return IsPrivate(address.MapToIPv4());

                return false;
            }

            byte[] v4 = address.GetAddressBytes();

            if (v4.Length != 4) return true;

            // 0.0.0.0/8
            if (v4[0] == 0) return true;
            // 10/8
            if (v4[0] == 10) return true;
            // 127/8
            if (v4[0] == 127) return true;
            // 169.254/16 (link-local, метаданные облаков)
            if (v4[0] == 169 && v4[1] == 254) return true;
            // 172.16/12
            if (v4[0] == 172 && (v4[1] & 0xF0) == 16) return true;
            // 192.168/16
            if (v4[0] == 192 && v4[1] == 168) return true;
            // 100.64/10 (CGNAT)
            if (v4[0] == 100 && (v4[1] & 0xC0) == 64) return true;
            // 198.18/15 (бенчмарк-сети)
            if (v4[0] == 198 && (v4[1] & 0xFE) == 18) return true;
            // 224/4 multicast и 240/4 reserved
            if (v4[0] >= 224) return true;

            return false;
        }

        // ------------------------------------------------------------- разбор

        /// <summary>Разбор списка из UI/JSON. Кривые строки отбрасываются.</summary>
        public static List<EgressRule> ParseRules(IEnumerable<string> items, out int dropped)
        {
            dropped = 0;

            List<EgressRule> rules = new List<EgressRule>();
            if (items == null) return rules;

            foreach (string item in items)
            {
                if (rules.Count >= MaxRules) { dropped++; continue; }

                EgressRule rule = EgressRule.Parse(item);
                if (rule == null) { dropped++; continue; }

                rules.Add(rule);
            }

            return rules;
        }

        public static string NormalizeMode(string mode)
        {
            switch ((mode ?? "").Trim().ToLowerInvariant())
            {
                case ModeSafe: return ModeSafe;
                case ModeAllowList:
                case "allow-list":
                case "allow": return ModeAllowList;
                default: return ModeAny;
            }
        }

        public Dictionary<string, object> ToDict()
        {
            List<object> allow = new List<object>();
            foreach (EgressRule rule in Allow) allow.Add(rule.Text);

            return Json.Obj("mode", Mode, "allow", allow);
        }

        public static EgressPolicy FromDict(Dictionary<string, object> map)
        {
            EgressPolicy policy = new EgressPolicy();
            if (map == null) return policy;

            policy.Mode = NormalizeMode(Json.GetString(map, "mode", ModeAny));

            List<object> allow = Json.GetList(map, "allow");
            List<string> items = new List<string>();

            if (allow != null)
                foreach (object item in allow)
                    if (item is string text) items.Add(text);

            int dropped;
            policy.Allow = ParseRules(items, out dropped);

            return policy;
        }

        public List<object> AllowView()
        {
            List<object> list = new List<object>();
            foreach (EgressRule rule in Allow) list.Add(rule.Text);
            return list;
        }

        public string Describe()
        {
            if (Mode == ModeAny) return "egress: any";

            if (Mode == ModeSafe) return "egress: safe (no LAN/loopback)";

            return "egress: allowlist (" + (Allow == null ? 0 : Allow.Count) + " rules)";
        }
    }

    /// <summary>Одно правило: цель (имя, IP, CIDR, *) и необязательный порт.</summary>
    public sealed class EgressRule
    {
        public string Text = "";

        private string _pattern;          // "*" | host | ip | cidr
        private bool _wildcard;
        private IPAddress _address;       // для одиночного IP
        private IpRange _range;           // для CIDR
        private int _port;                // 0 = любой

        public int Port { get { return _port; } }

        public static EgressRule Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;

            string trimmed = text.Trim();
            if (trimmed.Length > EgressPolicy.MaxRuleLength) return null;

            string pattern = trimmed;
            int port = 0;

            int colon = trimmed.LastIndexOf(':');

            if (colon > 0 && colon < trimmed.Length - 1)
            {
                string portText = trimmed.Substring(colon + 1);

                if (int.TryParse(portText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
                {
                    if (parsed <= 0 || parsed > 65535) return null;

                    port = parsed;
                    pattern = trimmed.Substring(0, colon);
                }
                else if (portText.IndexOf('.') < 0 && !portText.Contains("/"))
                {
                    // "host:" без номера - считаем опечаткой
                    return null;
                }
            }

            EgressRule rule = new EgressRule { Text = trimmed, _port = port };

            if (pattern == "*")
            {
                rule._wildcard = true;
                return rule;
            }

            if (pattern.Contains("/"))
            {
                rule._range = IpRange.Parse(pattern);
                if (rule._range == null) return null;
                rule._pattern = pattern;
                return rule;
            }

            if (IPAddress.TryParse(pattern, out IPAddress address))
            {
                rule._address = address;
                rule._pattern = pattern;
                return rule;
            }

            // имя хоста: буквы/цифры/точки/дефис
            foreach (char c in pattern)
            {
                bool ok = char.IsLetterOrDigit(c) || c == '.' || c == '-' || c == '_';
                if (!ok) return null;
            }

            if (pattern.IndexOf('.') < 0 && pattern != "localhost") return null;

            rule._pattern = pattern.ToLowerInvariant();
            return rule;
        }

        public bool Matches(string host, IPAddress address, int port)
        {
            if (_port != 0 && _port != port) return false;

            if (_wildcard) return true;

            if (_range != null) return _range.Contains(address);

            if (_address != null)
            {
                if (_address.Equals(address)) return true;

                if (address.IsIPv4MappedToIPv6 && _address.Equals(address.MapToIPv4())) return true;

                return false;
            }

            // имя: точное совпадение или поддомен
            if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(_pattern)) return false;

            string name = host.TrimEnd('.').ToLowerInvariant();

            return name == _pattern || name.EndsWith("." + _pattern, StringComparison.Ordinal);
        }
    }

    /// <summary>Минимальный CIDR: хватает для правил «10.0.0.0/8».</summary>
    public sealed class IpRange
    {
        private byte[] _network;
        private int _prefix;
        private AddressFamily _family;

        public static IpRange Parse(string text)
        {
            int slash = text.IndexOf('/');
            if (slash <= 0) return null;

            if (!IPAddress.TryParse(text.Substring(0, slash), out IPAddress address)) return null;

            if (!int.TryParse(text.Substring(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int prefix))
                return null;

            byte[] bytes = address.GetAddressBytes();
            int max = bytes.Length * 8;

            if (prefix < 0 || prefix > max) return null;

            IpRange range = new IpRange
            {
                _network = bytes,
                _prefix = prefix,
                _family = address.AddressFamily
            };

            return range;
        }

        public bool Contains(IPAddress address)
        {
            if (address == null) return false;

            if (address.AddressFamily != _family)
            {
                if (address.IsIPv4MappedToIPv6) return Contains(address.MapToIPv4());
                return false;
            }

            byte[] bytes = address.GetAddressBytes();
            if (bytes.Length != _network.Length) return false;

            int fullBytes = _prefix / 8;
            int restBits = _prefix % 8;

            for (int i = 0; i < fullBytes; i++)
                if (bytes[i] != _network[i]) return false;

            if (restBits > 0)
            {
                int mask = 0xFF << (8 - restBits);
                if ((bytes[fullBytes] & mask) != (_network[fullBytes] & mask)) return false;
            }

            return true;
        }
    }
}