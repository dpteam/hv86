using System;
using System.Collections.Generic;
using System.Globalization;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>
    /// Правило проброса TCP: host-порт → порт в госте.
    ///
    /// Зачем это вообще возможно: у v86 есть user-mode сеть (`fetch` backend)
    /// и публичный API `network_adapter.tcp_probe/connect`, который умеет
    /// открыть TCP-соединение к гостю из JS. Значит, достаточно слушать порт
    /// на хосте и гонять байты в обе стороны.
    ///
    /// По умолчанию слушаем только 127.0.0.1: гипервизор не должен случайно
    /// выставлять гостя в локальную сеть.
    /// </summary>
    public sealed class ForwardRule
    {
        public int HostPort;
        public int GuestPort;
        public string Bind = "127.0.0.1";

        public bool IsValid
        {
            get
            {
                return HostPort > 0 && HostPort <= 65535 &&
                       GuestPort > 0 && GuestPort <= 65535 &&
                       (Bind == "127.0.0.1" || Bind == "0.0.0.0" || Bind == "::1");
            }
        }

        public Dictionary<string, object> ToDict()
        {
            return Json.Obj(
                "host_port", HostPort,
                "guest_port", GuestPort,
                "bind", Bind);
        }

        public static ForwardRule FromDict(Dictionary<string, object> map)
        {
            if (map == null) return null;

            ForwardRule rule = new ForwardRule
            {
                HostPort = Json.GetInt(map, "host_port", 0),
                GuestPort = Json.GetInt(map, "guest_port", 0),
                Bind = Json.GetString(map, "bind", "127.0.0.1")
            };

            return rule.IsValid ? rule : null;
        }

        /// <summary>Разбор списка правил из JSON-сообщения. Невалидные молча отбрасываются.</summary>
        public static List<ForwardRule> ParseList(List<object> items, out int dropped)
        {
            dropped = 0;
            List<ForwardRule> rules = new List<ForwardRule>();

            if (items == null) return rules;

            HashSet<int> hostPorts = new HashSet<int>();

            foreach (object item in items)
            {
                ForwardRule rule = FromDict(item as Dictionary<string, object>);

                if (rule == null)
                {
                    dropped++;
                    continue;
                }

                // два правила на один host-порт - это гарантированный сюрприз
                if (!hostPorts.Add(rule.HostPort))
                {
                    dropped++;
                    continue;
                }

                rules.Add(rule);
            }

            return rules;
        }

        public string Describe()
        {
            return Bind + ":" + HostPort.ToString(CultureInfo.InvariantCulture) +
                   " → guest:" + GuestPort.ToString(CultureInfo.InvariantCulture);
        }
    }
}