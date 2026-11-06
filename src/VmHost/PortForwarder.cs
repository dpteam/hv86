using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hv86.Core;
using Hv86.Vm;

namespace Hv86.VmHost
{
    /// <summary>
    /// Проброс TCP-портов: слушаем порт на хосте и гоняем байты в гостя
    /// через v86 (`network_adapter.tcp_probe/connect` в странице консоли).
    ///
    /// Почему это вообще работает: у сети v86 (`fetch` backend) есть своя
    /// TCP-машина и публичный API подключения к порту гостя. Значит, проброс
    /// не требует ни TAP-драйвера, ни прав администратора на сеть - только
    /// сокет на localhost.
    ///
    /// Ограничение, которое надо помнить: пока гость не поднял сеть (DHCP),
    /// соединения будут отбиваться. Это видно в событиях как «refused».
    /// </summary>
    public sealed class PortForwarder : IDisposable
    {
        private const int ChunkSize = 32 * 1024;

        private readonly Action<Dictionary<string, object>> _post;
        private readonly object _gate = new object();
        private readonly List<TcpListener> _listeners = new List<TcpListener>();
        private readonly Dictionary<string, Session> _sessions = new Dictionary<string, Session>(StringComparer.Ordinal);
        private CancellationTokenSource _cts = new CancellationTokenSource();
        private bool _disposed;

        /// <summary>Человекочитаемые события проброса (слушаем/приняли/закрыли/ошибка).</summary>
        public event Action<string> Report;

        public PortForwarder(Action<Dictionary<string, object>> post)
        {
            _post = post;
        }

        // --------------------------------------------------------------- запуск

        public void Apply(List<ForwardRule> rules)
        {
            CloseListeners();

            if (rules == null || rules.Count == 0) return;

            lock (_gate)
            {
                if (_cts.IsCancellationRequested) _cts = new CancellationTokenSource();
            }

            CancellationToken token;
            lock (_gate) token = _cts.Token;

            foreach (ForwardRule rule in rules)
            {
                try
                {
                    IPAddress address = IPAddress.Parse(rule.Bind);
                    TcpListener listener = new TcpListener(address, rule.HostPort);
                    listener.Start(16);

                    lock (_gate) _listeners.Add(listener);

                    ForwardRule captured = rule;
                    _ = Task.Run(() => AcceptLoopAsync(listener, captured, token));

                    Announce(rule.Describe() + " — listening");
                }
                catch (Exception ex)
                {
                    Announce("cannot listen on " + rule.Describe() + ": " + ex.Message);
                }
            }
        }

        private async Task AcceptLoopAsync(TcpListener listener, ForwardRule rule, CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    if (token.IsCancellationRequested) return;

                    Log.Warn("NET", "accept " + rule.Describe() + ": " + ex.Message);
                    await Task.Delay(200, CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                Session session = new Session
                {
                    Id = Guid.NewGuid().ToString("N").Substring(0, 10),
                    Rule = rule,
                    Client = client
                };

                lock (_gate) _sessions[session.Id] = session;

                Announce("connection " + client.Client.RemoteEndPoint + " → guest:" + rule.GuestPort);

                // просим страницу открыть соединение к гостю
                _post(Json.Obj(
                    "action", "tcpConnect",
                    "session", session.Id,
                    "port", rule.GuestPort));

                // если гость не ответил (нет сети/DHCP), соединение не должно
                // висеть вечно: 30 секунд на установку, дальше - закрываем
                _ = Task.Run(async () =>
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);

                    Session pending;
                    lock (_gate) _sessions.TryGetValue(session.Id, out pending);

                    if (pending != null && !pending.Opened)
                    {
                        Announce("no answer from guest:" + rule.GuestPort + " (session " + session.Id + ")");
                        CloseSession(session.Id, "timeout", true);
                    }
                });

                _ = Task.Run(() => PumpClientAsync(session, token));
            }
        }

        // --------------------------------------------------------- клиент → гость

        private async Task PumpClientAsync(Session session, CancellationToken token)
        {
            byte[] buffer = new byte[ChunkSize];

            try
            {
                NetworkStream stream = session.Client.GetStream();

                while (!token.IsCancellationRequested)
                {
                    int read = await stream.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                    if (read <= 0) break;

                    byte[] chunk = new byte[read];
                    Buffer.BlockCopy(buffer, 0, chunk, 0, read);

                    _post(Json.Obj(
                        "action", "tcpWrite",
                        "session", session.Id,
                        "data", Convert.ToBase64String(chunk)));
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                Log.Debug("NET", "чтение с хоста " + session.Id + ": " + ex.Message);
            }
            finally
            {
                CloseSession(session.Id, "client closed", true);
            }
        }

        // --------------------------------------------------------- гость → клиент

        public void GuestData(string sessionId, string base64)
        {
            Session session;
            lock (_gate) _sessions.TryGetValue(sessionId, out session);

            if (session == null || string.IsNullOrEmpty(base64)) return;

            try
            {
                byte[] data = Convert.FromBase64String(base64);
                session.Client.GetStream().Write(data, 0, data.Length);
            }
            catch (Exception ex)
            {
                Log.Debug("NET", "запись в сокет " + sessionId + ": " + ex.Message);
                CloseSession(sessionId, "client write failed", true);
            }
        }

        public void GuestOpened(string sessionId)
        {
            Session session;
            lock (_gate) _sessions.TryGetValue(sessionId, out session);

            if (session == null) return;

            session.Opened = true;
            Announce("guest accepted, session " + sessionId);
        }

        public void GuestClosed(string sessionId, string reason)
        {
            Announce("guest closed session " + sessionId + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")"));

            Session session;
            lock (_gate) _sessions.TryGetValue(sessionId, out session);

            if (session == null) return;

            lock (_gate) _sessions.Remove(sessionId);

            try { session.Client.Close(); } catch { }
        }

        // ------------------------------------------------------------- закрытие

        private void CloseSession(string sessionId, string reason, bool tellPage)
        {
            Session session;
            lock (_gate)
            {
                if (!_sessions.TryGetValue(sessionId, out session)) return;
                _sessions.Remove(sessionId);
            }

            if (tellPage) _post(Json.Obj("action", "tcpClose", "session", sessionId));

            try { session.Client.Close(); } catch { }

            Log.Debug("NET", "сессия " + sessionId + " закрыта: " + reason);
        }

        private void CloseListeners()
        {
            TcpListener[] listeners;

            lock (_gate)
            {
                listeners = _listeners.ToArray();
                _listeners.Clear();
            }

            foreach (TcpListener listener in listeners)
            {
                try { listener.Stop(); } catch { }
            }
        }

        private void Announce(string text)
        {
            Log.Info("NET", text);

            try { Report?.Invoke(text); }
            catch { }
        }

        public void Stop()
        {
            try { _cts.Cancel(); } catch { }

            CloseListeners();

            Session[] sessions;

            lock (_gate)
            {
                sessions = new Session[_sessions.Count];
                _sessions.Values.CopyTo(sessions, 0);
                _sessions.Clear();
            }

            foreach (Session session in sessions)
            {
                _post(Json.Obj("action", "tcpClose", "session", session.Id));
                try { session.Client.Close(); } catch { }
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();

            try { _cts.Dispose(); } catch { }
        }

        private sealed class Session
        {
            public string Id;
            public ForwardRule Rule;
            public TcpClient Client;
            public bool Opened;
        }
    }
}