using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Hv86.Core
{
    /// <summary>
    /// Клиент named pipe. Один экземпляр = одно соединение; переподключение
    /// делает вызывающая сторона (EnsureConnectedAsync), потому что политика
    /// «когда переподключаться» у интерфейса и у vmhost разная.
    /// </summary>
    public sealed class PipeClient : IDisposable
    {
        private readonly string _name;
        private NamedPipeClientStream _pipe;
        private StreamWriter _writer;
        private StreamReader _reader;
        private readonly object _writeGate = new object();
        private readonly Dictionary<long, TaskCompletionSource<Dictionary<string, object>>> _pending =
            new Dictionary<long, TaskCompletionSource<Dictionary<string, object>>>();
        private readonly object _pendingGate = new object();
        private long _nextId;
        private CancellationTokenSource _cts;

        public PipeClient(string name)
        {
            _name = name;
        }

        /// <summary>Событие шины: имя + данные. Приходит из фонового потока.</summary>
        public event Action<string, Dictionary<string, object>> EventReceived;

        public bool IsConnected
        {
            get { try { return _pipe != null && _pipe.IsConnected; } catch { return false; } }
        }

        public string Name { get { return _name; } }

        public async Task<bool> ConnectAsync(int timeoutMs)
        {
            try
            {
                _pipe = new NamedPipeClientStream(".", _name, PipeDirection.InOut, PipeOptions.Asynchronous);
                await _pipe.ConnectAsync(timeoutMs).ConfigureAwait(false);

                _reader = new StreamReader(_pipe, new UTF8Encoding(false), false, 65536, true);
                _writer = new StreamWriter(_pipe, new UTF8Encoding(false), 65536, true) { AutoFlush = true };

                _cts = new CancellationTokenSource();
                _ = Task.Run(() => ReadLoopAsync(_cts.Token));

                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("PIPE", "подключение к " + _name + ": " + ex.Message);
                Dispose();
                return false;
            }
        }

        /// <summary>Ждёт подключения, повторяя попытки.</summary>
        public async Task<bool> EnsureConnectedAsync(int totalTimeoutMs)
        {
            if (IsConnected) return true;

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(Math.Max(500, totalTimeoutMs));

            while (DateTime.UtcNow < deadline)
            {
                if (await ConnectAsync(500).ConfigureAwait(false)) return true;
                await Task.Delay(150).ConfigureAwait(false);
            }

            return false;
        }

        private async Task ReadLoopAsync(CancellationToken token)
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    string line = await _reader.ReadLineAsync().ConfigureAwait(false);
                    if (line == null) break;
                    if (line.Length == 0) continue;

                    Dictionary<string, object> message = Json.ParseObjectSafe(line);
                    if (message == null) continue;

                    if (message.ContainsKey("event"))
                    {
                        Action<string, Dictionary<string, object>> handler = EventReceived;
                        if (handler != null)
                        {
                            try { handler(Json.GetString(message, "event", ""), Json.GetObject(message, "data")); }
                            catch (Exception ex) { Log.Warn("PIPE", "обработчик события: " + ex.Message); }
                        }

                        continue;
                    }

                    long id = Json.GetLong(message, "id", -1);
                    TaskCompletionSource<Dictionary<string, object>> source = null;

                    lock (_pendingGate)
                    {
                        if (id >= 0 && _pending.TryGetValue(id, out source))
                            _pending.Remove(id);
                    }

                    source?.TrySetResult(message);
                }
            }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested) Log.Debug("PIPE", "чтение: " + ex.Message);
            }
            finally
            {
                FailPending("канал закрыт");
            }
        }

        /// <summary>Вызов метода. Возвращает разобранный ответ (ok/result/error).</summary>
        public async Task<Dictionary<string, object>> CallAsync(string method,
            Dictionary<string, object> parameters, int timeoutMs)
        {
            if (!IsConnected) throw new Hv86Exception("нет соединения с " + _name);

            long id = Interlocked.Increment(ref _nextId);
            TaskCompletionSource<Dictionary<string, object>> source =
                new TaskCompletionSource<Dictionary<string, object>>(TaskCreationOptions.RunContinuationsAsynchronously);

            lock (_pendingGate) _pending[id] = source;

            string line = Json.Stringify(Json.Obj("id", id, "method", method, "params", parameters));

            try
            {
                lock (_writeGate) _writer.WriteLine(line);
            }
            catch (Exception ex)
            {
                lock (_pendingGate) _pending.Remove(id);
                throw new Hv86Exception("не удалось отправить запрос: " + ex.Message);
            }

            Task completed = await Task.WhenAny(source.Task, Task.Delay(Math.Max(1000, timeoutMs))).ConfigureAwait(false);

            if (completed != source.Task)
            {
                lock (_pendingGate) _pending.Remove(id);
                throw new Hv86Exception("нет ответа на " + method + " за " + timeoutMs + " мс");
            }

            return await source.Task.ConfigureAwait(false);
        }

        /// <summary>Вызов метода с проверкой ok: при ошибке бросает Hv86Exception.</summary>
        public async Task<object> CallForResultAsync(string method, Dictionary<string, object> parameters, int timeoutMs)
        {
            Dictionary<string, object> response = await CallAsync(method, parameters, timeoutMs).ConfigureAwait(false);

            if (!Json.GetBool(response, "ok", false))
                throw new Hv86Exception(Json.GetString(response, "error", "неизвестная ошибка"));

            object result;
            response.TryGetValue("result", out result);
            return result;
        }

        private void FailPending(string reason)
        {
            TaskCompletionSource<Dictionary<string, object>>[] all;
            lock (_pendingGate)
            {
                all = new TaskCompletionSource<Dictionary<string, object>>[_pending.Count];
                _pending.Values.CopyTo(all, 0);
                _pending.Clear();
            }

            foreach (TaskCompletionSource<Dictionary<string, object>> source in all)
                source.TrySetException(new Hv86Exception(reason));
        }

        public void Dispose()
        {
            try { _cts?.Cancel(); } catch { }
            try { _writer?.Dispose(); } catch { }
            try { _reader?.Dispose(); } catch { }
            try { _pipe?.Dispose(); } catch { }
            try { _cts?.Dispose(); } catch { }

            _writer = null;
            _reader = null;
            _pipe = null;
            _cts = null;
        }
    }
}
