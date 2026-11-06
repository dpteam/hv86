using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hv86.Core
{
    /// <summary>Имена каналов. Одно место, чтобы служба и хосты не разъехались.</summary>
    public static class PipeNames
    {
        public const string Service = "hv86";
        public static string VmHost(string vmId) { return "hv86-vm-" + vmId; }

        /// <summary>Канал горячей реплики (второй процесс той же ВМ).</summary>
        public static string VmReplica(string vmId) { return "hv86-vm-" + vmId + "-r"; }
    }

    /// <summary>Ошибка, которую можно показать пользователю как есть.</summary>
    public sealed class Hv86Exception : Exception
    {
        public Hv86Exception(string message) : base(message) { }
    }

    /// <summary>
    /// Сервер named pipe: построчный JSON-RPC 2.0-подобный протокол.
    ///
    ///   запрос:  {"id":1,"method":"vm.start","params":{...}}
    ///   ответ:   {"id":1,"ok":true,"result":{...}} | {"id":1,"ok":false,"error":"..."}
    ///   событие: {"event":"vm.state","data":{...}}
    ///
    /// Почему не HTTP: службе не нужен порт, файрвол и токен доступа; канал
    /// наследует ACL Windows. Удалённое управление - отдельная задача (M6).
    /// </summary>
    public sealed class PipeServer : IDisposable
    {
        private readonly string _name;
        private readonly Func<string, Dictionary<string, object>, Task<object>> _handler;
        private readonly List<Client> _clients = new List<Client>();
        private readonly object _gate = new object();
        private CancellationTokenSource _cts;

        public PipeServer(string name, Func<string, Dictionary<string, object>, Task<object>> handler)
        {
            _name = name;
            _handler = handler;
        }

        public int ClientCount
        {
            get { lock (_gate) return _clients.Count; }
        }

        public void Start()
        {
            _cts = new CancellationTokenSource();
            _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
            Log.Info("PIPE", "сервер \\\\.\\pipe\\" + _name + " слушает");
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = null;
                try
                {
                    pipe = new NamedPipeServerStream(_name, PipeDirection.InOut,
                        NamedPipeServerStream.MaxAllowedServerInstances,
                        PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);

                    Client client = new Client(pipe);
                    lock (_gate) _clients.Add(client);

                    Log.Debug("PIPE", "клиент подключился (" + ClientCount + ")");
                    _ = Task.Run(() => ReadLoopAsync(client, token));
                }
                catch (OperationCanceledException)
                {
                    try { pipe?.Dispose(); } catch { }
                    break;
                }
                catch (Exception ex)
                {
                    try { pipe?.Dispose(); } catch { }
                    if (token.IsCancellationRequested) break;

                    Log.Warn("PIPE", "приём соединения: " + ex.Message);

                    try { await Task.Delay(250, token).ConfigureAwait(false); }
                    catch { break; }
                }
            }
        }

        private async Task ReadLoopAsync(Client client, CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    string line = await client.Reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    if (line.Length == 0) continue;

                    await HandleLineAsync(client, line).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested) Log.Debug("PIPE", "чтение: " + ex.Message);
            }
            finally
            {
                lock (_gate) _clients.Remove(client);
                client.Dispose();
                Log.Debug("PIPE", "клиент отключился (" + ClientCount + ")");
            }
        }

        private async Task HandleLineAsync(Client client, string line)
        {
            Dictionary<string, object> request = Json.ParseObjectSafe(line);
            if (request == null)
            {
                client.Send(Json.Stringify(Json.Obj("id", 0, "ok", false, "error", "bad-json")));
                return;
            }

            long id = Json.GetLong(request, "id", 0);
            string method = Json.GetString(request, "method", "");
            Dictionary<string, object> parameters = Json.GetObject(request, "params");

            if (string.IsNullOrEmpty(method))
            {
                client.Send(Json.Stringify(Json.Obj("id", id, "ok", false, "error", "no-method")));
                return;
            }

            Dictionary<string, object> response = new Dictionary<string, object>(StringComparer.Ordinal);
            response["id"] = id;

            try
            {
                object result = await _handler(method, parameters ?? new Dictionary<string, object>()).ConfigureAwait(false);
                response["ok"] = true;
                response["result"] = result;
            }
            catch (Hv86Exception ex)
            {
                response["ok"] = false;
                response["error"] = ex.Message;
            }
            catch (Exception ex)
            {
                Log.Exception("PIPE", "метод " + method, ex);
                response["ok"] = false;
                response["error"] = ex.Message;
            }

            client.Send(Json.Stringify(response));
        }

        public void Broadcast(string eventName, object data)
        {
            if (string.IsNullOrEmpty(eventName)) return;

            string line = Json.Stringify(Json.Obj("event", eventName, "data", data));
            Client[] snapshot;

            lock (_gate) snapshot = _clients.ToArray();

            foreach (Client client in snapshot) client.Send(line);
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }

            Client[] snapshot;
            lock (_gate)
            {
                snapshot = _clients.ToArray();
                _clients.Clear();
            }

            foreach (Client client in snapshot) client.Dispose();
            try { _cts?.Dispose(); } catch { }
            _cts = null;
        }

        private sealed class Client : IDisposable
        {
            /// <summary>Сколько сообщений держим в очереди, прежде чем выбрасывать старые.</summary>
            private const int MaxQueued = 512;

            private readonly NamedPipeServerStream _pipe;
            private readonly object _writeGate = new object();

            // Очередь и свой поток записи: Broadcast вызывается из UI-потока
            // vmhost, а WriteLine в заполненный канал блокируется навсегда, если
            // клиент перестал читать. Тогда окно ВМ перестаёт отвечать - именно
            // так и выглядел баг "не закрыть окно, пока ВМ работает".
            private readonly System.Collections.Concurrent.ConcurrentQueue<string> _queue =
                new System.Collections.Concurrent.ConcurrentQueue<string>();

            private readonly SemaphoreSlim _signal = new SemaphoreSlim(0);
            private readonly Task _writer;
            private volatile bool _broken;

            public Client(NamedPipeServerStream pipe)
            {
                _pipe = pipe;
                Reader = new StreamReader(pipe, new UTF8Encoding(false), false, 65536, true);
                Writer = new StreamWriter(pipe, new UTF8Encoding(false), 65536, true) { AutoFlush = true };

                _writer = Task.Run(() => WriteLoopAsync());
            }

            public StreamReader Reader { get; private set; }
            public StreamWriter Writer { get; private set; }

            public void Send(string line)
            {
                if (string.IsNullOrEmpty(line) || _broken) return;

                _queue.Enqueue(line);

                // Переполнение: выбрасываем самое старое. Обычно это устаревшие
                // метрики, а свежие важнее.
                while (_queue.Count > MaxQueued && _queue.TryDequeue(out _))
                {
                }

                try { _signal.Release(); }
                catch (ObjectDisposedException) { }
                catch (SemaphoreFullException) { }
            }

            private async Task WriteLoopAsync()
            {
                while (!_broken)
                {
                    try
                    {
                        await _signal.WaitAsync().ConfigureAwait(false);
                    }
                    catch (ObjectDisposedException)
                    {
                        return;
                    }

                    while (_queue.TryDequeue(out string line))
                    {
                        lock (_writeGate)
                        {
                            try { Writer.WriteLine(line); }
                            catch (Exception ex)
                            {
                                Log.Debug("PIPE", "запись: " + ex.Message);
                                _broken = true;
                                return;
                            }
                        }
                    }
                }
            }

            public void Dispose()
            {
                _broken = true;

                try { _signal.Release(); } catch { }

                try { _writer?.Wait(1000); } catch { }

                try { Reader?.Dispose(); } catch { }
                try { Writer?.Dispose(); } catch { }
                try { _pipe?.Dispose(); } catch { }
                try { _signal.Dispose(); } catch { }
            }
        }
    }
}
