using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Hv86.Core;

namespace Hv86.VmHost
{
    /// <summary>
    /// WISP-сервер (protocol.md v1.2) для сети v86.
    ///
    /// Зачем свой: `fetch`-backend умеет наружу только HTTP(S) и режет всё
    /// остальное (apt, git, ssh, SMTP). WISP даёт гостю произвольный TCP:
    /// страница просит «соедини меня с host:port», а соединение открываем мы.
    ///
    /// Реализация намеренно без HttpListener: тот идёт через http.sys и требует
    /// URL ACL (в службе LocalSystem это ок, а vmhost живёт под обычным
    /// пользователем). Поэтому WebSocket-хендшейк и кадры разбираем сами -
    /// это ~150 строк и ноль зависимостей.
    ///
    /// Работает только когда ВМ в режиме net.mode=relay: в режиме nat нужен
    /// fetch-backend, иначе ломается проброс портов host → guest.
    /// </summary>
    public sealed class WispRelay : IDisposable
    {
        private const int MaxBufferPackets = 64;
        private const int ConnectTimeoutMs = 12000;
        private const int MaxMessageSize = 1024 * 1024;
        private const int MaxStreamsPerConnection = 128;

        private const byte PacketConnect = 0x01;
        private const byte PacketData = 0x02;
        private const byte PacketContinue = 0x03;
        private const byte PacketClose = 0x04;

        private const byte StreamTcp = 0x01;
        private const byte StreamUdp = 0x02;

        private TcpListener _listener;
        private CancellationTokenSource _cts;
        private readonly List<Connection> _connections = new List<Connection>();
        private readonly object _gate = new object();
        private bool _disposed;
        private int _handshakes;

        public int Port { get; private set; }

        /// <summary>Сколько websocket-клиентов успешно подключилось (для диагностики).</summary>
        public int Handshakes { get { return Volatile.Read(ref _handshakes); } }

        public bool Start(out string error)
        {
            error = null;

            try
            {
                TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
                listener.Start();

                Port = ((IPEndPoint)listener.LocalEndpoint).Port;

                _listener = listener;
                _cts = new CancellationTokenSource();

                _ = Task.Run(() => AcceptLoopAsync(_cts.Token));

                Log.Info("NET", "wisp relay слушает ws://127.0.0.1:" + Port + "/");
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                Log.Error("NET", "wisp relay не запустился: " + ex.Message);
                return false;
            }
        }

        private async Task AcceptLoopAsync(CancellationToken token)
        {
            while (!token.IsCancellationRequested)
            {
                TcpClient client;

                try
                {
                    client = await _listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
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
                    Log.Warn("NET", "relay accept: " + ex.Message);
                    continue;
                }

                client.NoDelay = true;

                Connection connection = new Connection(client, token,
                    () => Interlocked.Increment(ref _handshakes));

                lock (_gate) _connections.Add(connection);

                _ = Task.Run(async () =>
                {
                    try
                    {
                        await connection.RunAsync().ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Log.Debug("NET", "relay connection: " + ex.Message);
                    }
                    finally
                    {
                        lock (_gate) _connections.Remove(connection);
                        connection.Dispose();
                    }
                });
            }
        }

        public void Stop()
        {
            try { _cts?.Cancel(); } catch { }

            try { _listener?.Stop(); } catch { }
            _listener = null;

            Connection[] connections;
            lock (_gate)
            {
                connections = _connections.ToArray();
                _connections.Clear();
            }

            foreach (Connection connection in connections)
                try { connection.Dispose(); } catch { }

            Log.Info("NET", "wisp relay остановлен");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Stop();

            try { _cts?.Dispose(); } catch { }
        }

        // ------------------------------------------------------------ соединение

        private sealed class Connection : IDisposable
        {
            private readonly TcpClient _client;
            private readonly NetworkStream _stream;
            private readonly CancellationToken _token;
            private readonly Action _relayHandshake;
            private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
            private readonly Dictionary<uint, StreamState> _streams = new Dictionary<uint, StreamState>();
            private readonly object _gate = new object();
            private bool _closed;

            public Connection(TcpClient client, CancellationToken token, Action onHandshake)
            {
                _client = client;
                _stream = client.GetStream();
                _token = token;
                _relayHandshake = onHandshake;
            }

            public async Task RunAsync()
            {
                if (!await HandshakeAsync().ConfigureAwait(false))
                {
                    Log.Warn("NET", "relay: клиент не прошёл websocket-хендшейк");
                    return;
                }

                _relayHandshake?.Invoke();

                // по протоколу: сразу после хендшейка - CONTINUE для stream 0
                await SendAsync(PacketContinue, 0, UInt32(MaxBufferPackets)).ConfigureAwait(false);

                while (!_token.IsCancellationRequested && !_closed)
                {
                    byte[] message = await ReadMessageAsync().ConfigureAwait(false);
                    if (message == null) break;

                    Handle(message);
                }
            }

            // -------------------------------------------------------- handshake

            private async Task<bool> HandshakeAsync()
            {
                MemoryStream request = new MemoryStream();
                byte[] buffer = new byte[1024];
                int sep = -1;

                while (request.Length < 16 * 1024)
                {
                    int read = await _stream.ReadAsync(buffer, 0, buffer.Length, _token).ConfigureAwait(false);
                    if (read <= 0) return false;

                    request.Write(buffer, 0, read);

                    byte[] data = request.GetBuffer();
                    int length = (int)request.Length;
                    sep = FindHeaderEnd(data, length);

                    if (sep >= 0) break;
                }

                if (sep < 0) return false;

                string headers = Encoding.ASCII.GetString(request.GetBuffer(), 0, sep);

                if (headers.IndexOf("upgrade: websocket", StringComparison.OrdinalIgnoreCase) < 0)
                    return false;

                string key = Header(headers, "Sec-WebSocket-Key");
                if (string.IsNullOrEmpty(key)) return false;

                string accept;
                using (SHA1 sha1 = SHA1.Create())
                {
                    byte[] hash = sha1.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11"));
                    accept = Convert.ToBase64String(hash);
                }

                string response =
                    "HTTP/1.1 101 Switching Protocols\r\n" +
                    "Upgrade: websocket\r\n" +
                    "Connection: Upgrade\r\n" +
                    "Sec-WebSocket-Accept: " + accept + "\r\n\r\n";

                byte[] responseBytes = Encoding.ASCII.GetBytes(response);
                await _stream.WriteAsync(responseBytes, 0, responseBytes.Length, _token).ConfigureAwait(false);
                await _stream.FlushAsync(_token).ConfigureAwait(false);

                return true;
            }

            private static int FindHeaderEnd(byte[] data, int length)
            {
                for (int i = 3; i < length; i++)
                {
                    if (data[i] == 10 && data[i - 1] == 13 && data[i - 2] == 10 && data[i - 3] == 13)
                        return i - 3;
                }

                return -1;
            }

            private static string Header(string headers, string name)
            {
                foreach (string line in headers.Split('\n'))
                {
                    int colon = line.IndexOf(':');
                    if (colon <= 0) continue;

                    if (string.Equals(line.Substring(0, colon).Trim(), name, StringComparison.OrdinalIgnoreCase))
                        return line.Substring(colon + 1).Trim();
                }

                return null;
            }

            // ------------------------------------------------------------ кадры

            private async Task<byte[]> ReadMessageAsync()
            {
                MemoryStream message = null;

                while (!_token.IsCancellationRequested)
                {
                    byte[] header = await ReadExactAsync(2).ConfigureAwait(false);
                    if (header == null) return null;

                    bool fin = (header[0] & 0x80) != 0;
                    int opcode = header[0] & 0x0F;
                    bool masked = (header[1] & 0x80) != 0;
                    long length = header[1] & 0x7F;

                    if (length == 126)
                    {
                        byte[] ext = await ReadExactAsync(2).ConfigureAwait(false);
                        if (ext == null) return null;
                        length = (ext[0] << 8) | ext[1];
                    }
                    else if (length == 127)
                    {
                        byte[] ext = await ReadExactAsync(8).ConfigureAwait(false);
                        if (ext == null) return null;

                        length = 0;
                        for (int i = 0; i < 8; i++) length = (length << 8) | ext[i];
                    }

                    if (length < 0 || length > MaxMessageSize)
                        throw new Hv86Exception("relay: слишком большой websocket-кадр");

                    byte[] mask = null;
                    if (masked)
                    {
                        mask = await ReadExactAsync(4).ConfigureAwait(false);
                        if (mask == null) return null;
                    }

                    byte[] payload = await ReadExactAsync((int)length).ConfigureAwait(false);
                    if (payload == null) return null;

                    if (masked)
                        for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

                    if (opcode == 0x8) return null;                       // close
                    if (opcode == 0x9) { await SendFrameAsync(0xA, payload).ConfigureAwait(false); continue; } // ping → pong
                    if (opcode == 0xA) continue;                          // pong

                    if (message == null) message = new MemoryStream();
                    message.Write(payload, 0, payload.Length);

                    if (fin) return message.ToArray();
                }

                return null;
            }

            private async Task<byte[]> ReadExactAsync(int count)
            {
                if (count == 0) return new byte[0];

                byte[] buffer = new byte[count];
                int read = 0;

                while (read < count)
                {
                    int chunk;

                    try
                    {
                        chunk = await _stream.ReadAsync(buffer, read, count - read, _token).ConfigureAwait(false);
                    }
                    catch
                    {
                        return null;
                    }

                    if (chunk <= 0) return null;
                    read += chunk;
                }

                return buffer;
            }

            private async Task SendFrameAsync(int opcode, byte[] payload)
            {
                await _sendLock.WaitAsync(_token).ConfigureAwait(false);

                try
                {
                    byte[] header;

                    if (payload.Length < 126)
                    {
                        header = new byte[2];
                        header[0] = (byte)(0x80 | opcode);
                        header[1] = (byte)payload.Length;
                    }
                    else if (payload.Length < 65536)
                    {
                        header = new byte[4];
                        header[0] = (byte)(0x80 | opcode);
                        header[1] = 126;
                        header[2] = (byte)(payload.Length >> 8);
                        header[3] = (byte)payload.Length;
                    }
                    else
                    {
                        header = new byte[10];
                        header[0] = (byte)(0x80 | opcode);
                        header[1] = 127;

                        long length = payload.Length;
                        for (int i = 0; i < 8; i++)
                            header[2 + i] = (byte)(length >> (8 * (7 - i)));
                    }

                    await _stream.WriteAsync(header, 0, header.Length, _token).ConfigureAwait(false);
                    await _stream.WriteAsync(payload, 0, payload.Length, _token).ConfigureAwait(false);
                    await _stream.FlushAsync(_token).ConfigureAwait(false);
                }
                catch
                {
                    _closed = true;
                }
                finally
                {
                    _sendLock.Release();
                }
            }

            private Task SendAsync(byte type, uint id, byte[] payload)
            {
                byte[] packet = new byte[5 + (payload == null ? 0 : payload.Length)];
                packet[0] = type;
                BitConverter.TryWriteBytes(new Span<byte>(packet, 1, 4), id);

                if (payload != null && payload.Length > 0) payload.CopyTo(packet, 5);

                return SendFrameAsync(0x2, packet);
            }

            private static byte[] UInt32(int value)
            {
                byte[] buffer = new byte[4];
                BitConverter.TryWriteBytes(buffer, (uint)value);
                return buffer;
            }

            // ------------------------------------------------------------ WISP

            private void Handle(byte[] packet)
            {
                if (packet.Length < 5) return;

                byte type = packet[0];
                uint id = BitConverter.ToUInt32(packet, 1);

                int payloadLength = packet.Length - 5;

                switch (type)
                {
                    case PacketConnect:
                        if (payloadLength < 4)
                        {
                            _ = SendAsync(PacketClose, id, new[] { (byte)0x41 });
                            return;
                        }

                        OpenStream(id, packet[5], packet[6] | (packet[7] << 8),
                            Encoding.UTF8.GetString(packet, 8, payloadLength - 3));
                        break;

                    case PacketData:
                    {
                        StreamState state;
                        lock (_gate) _streams.TryGetValue(id, out state);
                        if (state == null) return;

                        byte[] data = new byte[payloadLength];
                        Buffer.BlockCopy(packet, 5, data, 0, payloadLength);

                        state.Write(data);
                        break;
                    }

                    case PacketClose:
                    {
                        StreamState state;
                        lock (_gate)
                        {
                            if (_streams.TryGetValue(id, out state)) _streams.Remove(id);
                        }

                        state?.Dispose();
                        break;
                    }

                    case PacketContinue:
                        break;   // серверу нечего учитывать
                }
            }

            private void OpenStream(uint id, byte streamType, int port, string host)
            {
                int count;
                lock (_gate) count = _streams.Count;

                if (count >= MaxStreamsPerConnection)
                {
                    _ = SendAsync(PacketClose, id, new[] { (byte)0x49 });
                    return;
                }

                if (port <= 0 || port > 65535 || string.IsNullOrWhiteSpace(host) || host.Length > 255 ||
                    (streamType != StreamTcp && streamType != StreamUdp))
                {
                    _ = SendAsync(PacketClose, id, new[] { (byte)0x41 });
                    return;
                }

                foreach (char c in host)
                {
                    if (char.IsControl(c) || c == '/' || c == '\\')
                    {
                        _ = SendAsync(PacketClose, id, new[] { (byte)0x41 });
                        return;
                    }
                }

                StreamState state = new StreamState(id, streamType == StreamUdp, host, port,
                    data => SendAsync(PacketData, id, data),
                    () => SendAsync(PacketContinue, id, UInt32(MaxBufferPackets)),
                    reason => CloseStream(id, reason));

                lock (_gate) _streams[id] = state;

                // клиент может начинать слать данные сразу, но буфер учитывает -
                // поэтому CONTINUE отправляем до первого DATA
                if (streamType != StreamUdp)
                    _ = SendAsync(PacketContinue, id, UInt32(MaxBufferPackets));

                _ = Task.Run(() => state.OpenAsync(_token));
            }

            private void CloseStream(uint id, byte reason)
            {
                StreamState state;

                lock (_gate)
                {
                    if (!_streams.TryGetValue(id, out state)) return;
                    _streams.Remove(id);
                }

                _ = SendAsync(PacketClose, id, new[] { reason });
                state?.Dispose();
            }

            public void Dispose()
            {
                if (_closed) return;
                _closed = true;

                StreamState[] streams;

                lock (_gate)
                {
                    streams = new StreamState[_streams.Count];
                    _streams.Values.CopyTo(streams, 0);
                    _streams.Clear();
                }

                foreach (StreamState state in streams)
                    try { state.Dispose(); } catch { }

                try { _stream.Close(); } catch { }
                try { _client.Close(); } catch { }
                try { _sendLock.Dispose(); } catch { }
            }
        }

        // --------------------------------------------------------------- поток

        private sealed class StreamState : IDisposable
        {
            private const int MaxQueuedBytes = 4 * 1024 * 1024;

            private readonly uint _id;
            private readonly bool _udp;
            private readonly string _host;
            private readonly int _port;
            private readonly Func<byte[], Task> _toClient;
            private readonly Func<Task> _continue;
            private readonly Action<byte> _close;
            private readonly ConcurrentQueue<byte[]> _queue = new ConcurrentQueue<byte[]>();
            private readonly CancellationTokenSource _cts = new CancellationTokenSource();

            private TcpClient _tcp;
            private UdpClient _udpClient;
            private NetworkStream _net;
            private int _queuedBytes;
            private int _written;
            private bool _ready;
            private bool _disposed;

            public StreamState(uint id, bool udp, string host, int port,
                Func<byte[], Task> toClient, Func<Task> onContinue, Action<byte> close)
            {
                _id = id;
                _udp = udp;
                _host = host;
                _port = port;
                _toClient = toClient;
                _continue = onContinue;
                _close = close;
            }

            public async Task OpenAsync(CancellationToken token)
            {
                try
                {
                    using (CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(token))
                    {
                        timeout.CancelAfter(ConnectTimeoutMs);

                        if (_udp)
                        {
                            _udpClient = new UdpClient();
                            _udpClient.Connect(_host, _port);
                        }
                        else
                        {
                            _tcp = new TcpClient { NoDelay = true };
                            await _tcp.ConnectAsync(_host, _port, timeout.Token).ConfigureAwait(false);
                            _net = _tcp.GetStream();
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Info("NET", "relay: " + _host + ":" + _port + " — " + ex.Message);
                    _close(ReasonFor(ex));
                    return;
                }

                _ready = true;

                while (_queue.TryDequeue(out byte[] queued))
                {
                    _queuedBytes -= queued.Length;
                    if (!await WriteRawAsync(queued).ConfigureAwait(false)) return;
                }

                _ = Task.Run(() => ReadLoopAsync(_cts.Token));
            }

            public void Write(byte[] data)
            {
                if (_disposed) return;

                if (!_ready)
                {
                    if (_queuedBytes + data.Length > MaxQueuedBytes)
                    {
                        _close(0x49);
                        return;
                    }

                    _queuedBytes += data.Length;
                    _queue.Enqueue(data);
                    return;
                }

                _ = Task.Run(async () =>
                {
                    if (!await WriteRawAsync(data).ConfigureAwait(false)) return;

                    // по протоколу клиент ждёт CONTINUE, когда исчерпает буфер
                    if (Interlocked.Increment(ref _written) >= MaxBufferPackets)
                    {
                        Interlocked.Exchange(ref _written, 0);

                        if (_continue != null)
                            await _continue().ConfigureAwait(false);
                    }
                });
            }

            private async Task<bool> WriteRawAsync(byte[] data)
            {
                try
                {
                    if (_udp)
                    {
                        await _udpClient.SendAsync(new ReadOnlyMemory<byte>(data), null, _cts.Token)
                            .ConfigureAwait(false);
                    }
                    else
                    {
                        await _net.WriteAsync(data, 0, data.Length, _cts.Token).ConfigureAwait(false);
                        await _net.FlushAsync(_cts.Token).ConfigureAwait(false);
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    Log.Debug("NET", "relay write: " + ex.Message);
                    _close(0x47);
                    return false;
                }
            }

            private async Task ReadLoopAsync(CancellationToken token)
            {
                byte[] buffer = new byte[32 * 1024];

                try
                {
                    while (!token.IsCancellationRequested)
                    {
                        int read;

                        if (_udp)
                        {
                            UdpReceiveResult result = await _udpClient.ReceiveAsync(token).ConfigureAwait(false);
                            await _toClient(result.Buffer).ConfigureAwait(false);
                            continue;
                        }

                        read = await _net.ReadAsync(buffer, 0, buffer.Length, token).ConfigureAwait(false);
                        if (read <= 0) break;

                        byte[] data = new byte[read];
                        Buffer.BlockCopy(buffer, 0, data, 0, read);

                        await _toClient(data).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Log.Debug("NET", "relay read: " + ex.Message);
                }

                if (!_disposed) _close(0x02);
            }

            private static byte ReasonFor(Exception ex)
            {
                SocketException socket = ex as SocketException ?? ex.InnerException as SocketException;

                if (socket == null)
                    return ex is OperationCanceledException ? (byte)0x43 : (byte)0x41;

                switch (socket.SocketErrorCode)
                {
                    case SocketError.HostNotFound:
                    case SocketError.NoData:
                        return 0x42;
                    case SocketError.ConnectionRefused:
                        return 0x44;
                    case SocketError.TimedOut:
                    case SocketError.HostUnreachable:
                    case SocketError.NetworkUnreachable:
                        return 0x43;
                    default:
                        return 0x41;
                }
            }

            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;

                try { _cts.Cancel(); } catch { }
                try { _net?.Close(); } catch { }
                try { _tcp?.Close(); } catch { }
                try { _udpClient?.Close(); } catch { }
                try { _cts.Dispose(); } catch { }
            }
        }
    }
}