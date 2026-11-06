using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using Hv86.Core;

namespace Hv86.App
{
    /// <summary>
    /// Мост между страницей в WebView2 и ядром.
    ///
    /// Правила (AGENTS.md):
    ///   * единственный вход - Handle(json) из WebMessageReceived;
    ///   * единственный выход - Send(dict) через chrome.webview.postMessage;
    ///   * никакой бизнес-логики: только маршрутизация и сериализация.
    ///
    /// Формат:
    ///   { "action": "имя", ...параметры }   страница -> хост
    ///   { "type":   "имя", ...данные }      хост -> страница
    /// </summary>
    public sealed class WebBridge
    {
        private readonly Action<Dictionary<string, object>> _post;
        private readonly Dictionary<string, Func<Dictionary<string, object>, Task>> _handlers =
            new Dictionary<string, Func<Dictionary<string, object>, Task>>(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new object();

        public WebBridge(Action<Dictionary<string, object>> post)
        {
            _post = post;
        }

        public void Register(string action, Action<Dictionary<string, object>> handler)
        {
            if (handler == null) return;
            Register(action, delegate (Dictionary<string, object> message)
            {
                handler(message);
                return Task.CompletedTask;
            });
        }

        public void Register(string action, Func<Dictionary<string, object>, Task> handler)
        {
            if (string.IsNullOrEmpty(action) || handler == null) return;
            lock (_gate) _handlers[action] = handler;
        }

        /// <summary>Вызывается из OnWebMessage. Никогда не бросает наружу.</summary>
        public void Handle(string json)
        {
            Dictionary<string, object> message = Json.ParseObjectSafe(json);
            if (message == null)
            {
                Log.Warn("UI", "нераспознанное сообщение из страницы (" + (json == null ? 0 : json.Length) + " символов)");
                SendError("bad-message");
                return;
            }

            string action = Json.GetString(message, "action", "");
            if (string.IsNullOrEmpty(action))
            {
                SendError("no-action");
                return;
            }

            Func<Dictionary<string, object>, Task> handler;
            lock (_gate) _handlers.TryGetValue(action, out handler);

            if (handler == null)
            {
                Log.Warn("UI", "неизвестное действие: " + action);
                Send(Msg("error", "message", "unknown action: " + action));
                return;
            }

            // Обработчик асинхронный, но вызывается из UI-потока WebView2:
            // результат отправляется через Send() уже из потока-продолжения,
            // а MainForm.PostToPage сам делает BeginInvoke.
            Task.Run(async () =>
            {
                try
                {
                    await handler(message).ConfigureAwait(false);
                }
                catch (Hv86Exception ex)
                {
                    Send(Msg("error", "message", ex.Message, "action", action));
                }
                catch (Exception ex)
                {
                    Log.Exception("UI", "обработчик '" + action + "'", ex);
                    Send(Msg("error", "message", ex.Message, "action", action));
                }
            });
        }

        public void Send(Dictionary<string, object> payload)
        {
            if (payload == null) return;
            try { _post(payload); }
            catch (Exception ex) { Log.Warn("UI", "post: " + ex.Message); }
        }

        public void SendError(string code)
        {
            Send(Msg("error", "message", code));
        }

        public static Dictionary<string, object> Msg(string type, params object[] pairs)
        {
            Dictionary<string, object> map = new Dictionary<string, object>(StringComparer.Ordinal);
            if (type != null) map["type"] = type;

            for (int i = 0; i + 1 < pairs.Length; i += 2)
                map[Convert.ToString(pairs[i], CultureInfo.InvariantCulture)] = pairs[i + 1];

            return map;
        }
    }
}