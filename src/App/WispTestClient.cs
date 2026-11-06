using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Hv86.App
{
    /// <summary>
    /// Минимальный WISP-клиент для самотестов.
    ///
    /// Проверяет ровно то, ради чего написан relay: websocket-хендшейк,
    /// CONNECT к произвольному host:port и обмен DATA в обе стороны.
    /// Никакого отношения к v86 - если этот тест проходит, релей живой,
    /// и вопрос остаётся только к сетевому стеку гостя.
    /// </summary>
    public static class WispTestClient
    {
        public static bool Echo(int relayPort, int targetPort, string payload, out string detail)
        {
            detail = "";

            try
            {
                using (TcpClient client = new TcpClient { NoDelay = true })
                {
                    client.Connect("127.0.0.1", relayPort);
                    client.ReceiveTimeout = 15000;

                    NetworkStream stream = client.GetStream();

                    string key = Convert.ToBase64String(Guid.NewGuid().ToByteArray());

                    string request =
                        "GET / HTTP/1.1\r\n" +
                        "Host: 127.0.0.1:" + relayPort + "\r\n" +
                        "Upgrade: websocket\r\n" +
                        "Connection: Upgrade\r\n" +
                        "Sec-WebSocket-Key: " + key + "\r\n" +
                        "Sec-WebSocket-Version: 13\r\n\r\n";

                    byte[] requestBytes = Encoding.ASCII.GetBytes(request);
                    stream.Write(requestBytes, 0, requestBytes.Length);
                    stream.Flush();

                    string response = ReadHeaders(stream);

                    if (response.IndexOf("101", StringComparison.Ordinal) < 0)
                    {
                        detail = "handshake failed: " + FirstLine(response);
                        return false;
                    }

                    // сервер обязан первым прислать CONTINUE для stream 0
                    byte[] hello = ReadMessage(stream);
                    if (hello == null || hello.Length < 5 || hello[0] != 0x03)
                    {
                        detail = "no initial CONTINUE packet";
                        return false;
                    }

                    byte[] connect = Packet(0x01, 1, ConnectPayload(targetPort, "127.0.0.1"));
                    SendMessage(stream, connect);

                    byte[] data = Packet(0x02, 1, Encoding.UTF8.GetBytes(payload));
                    SendMessage(stream, data);

                    byte[] expected = Encoding.UTF8.GetBytes(payload);
                    DateTime deadline = DateTime.UtcNow.AddSeconds(15);

                    while (DateTime.UtcNow < deadline)
                    {
                        byte[] message = ReadMessage(stream);
                        if (message == null) break;

                        if (message.Length < 5) continue;

                        uint id = BitConverter.ToUInt32(message, 1);
                        byte type = message[0];

                        if (type == 0x04)
                        {
                            detail = "relay closed the stream with reason 0x" + message[5].ToString("x2");
                            return false;
                        }

                        if (type != 0x02 || id != 1) continue;

                        if (message.Length - 5 != expected.Length)
                        {
                            detail = "unexpected payload length " + (message.Length - 5);
                            return false;
                        }

                        for (int i = 0; i < expected.Length; i++)
                        {
                            if (message[5 + i] != expected[i])
                            {
                                detail = "payload mismatch";
                                return false;
                            }
                        }

                        detail = "echo through CONNECT/CLOSE worked (" + expected.Length + " bytes)";
                        return true;
                    }

                    detail = "no DATA came back within 15 s";
                    return false;
                }
            }
            catch (Exception ex)
            {
                detail = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        // --------------------------------------------------------------- WISP

        private static byte[] Packet(byte type, uint id, byte[] payload)
        {
            byte[] packet = new byte[5 + (payload == null ? 0 : payload.Length)];
            packet[0] = type;
            BitConverter.TryWriteBytes(new Span<byte>(packet, 1, 4), id);

            if (payload != null && payload.Length > 0) payload.CopyTo(packet, 5);
            return packet;
        }

        private static byte[] ConnectPayload(int port, string host)
        {
            byte[] hostBytes = Encoding.UTF8.GetBytes(host);
            byte[] payload = new byte[3 + hostBytes.Length];

            payload[0] = 0x01;                     // TCP
            payload[1] = (byte)(port & 0xFF);
            payload[2] = (byte)((port >> 8) & 0xFF);
            hostBytes.CopyTo(payload, 3);

            return payload;
        }

        // ---------------------------------------------------------- websocket

        private static string ReadHeaders(NetworkStream stream)
        {
            MemoryStream buffer = new MemoryStream();
            byte[] one = new byte[1];
            List<byte> tail = new List<byte>();

            while (buffer.Length < 8192)
            {
                int read = stream.Read(one, 0, 1);
                if (read <= 0) break;

                buffer.WriteByte(one[0]);

                tail.Add(one[0]);
                if (tail.Count > 4) tail.RemoveAt(0);

                if (tail.Count == 4 && tail[0] == 13 && tail[1] == 10 && tail[2] == 13 && tail[3] == 10)
                    break;
            }

            return Encoding.ASCII.GetString(buffer.ToArray());
        }

        private static string FirstLine(string response)
        {
            int nl = response.IndexOf("\r\n", StringComparison.Ordinal);
            return nl > 0 ? response.Substring(0, nl) : response.Trim();
        }

        private static void SendMessage(NetworkStream stream, byte[] payload)
        {
            byte[] mask = new byte[4];
            using (RandomNumberGenerator rng = RandomNumberGenerator.Create()) rng.GetBytes(mask);

            byte[] header;

            if (payload.Length < 126)
            {
                header = new byte[6];
                header[0] = 0x82;
                header[1] = (byte)(0x80 | payload.Length);
            }
            else if (payload.Length < 65536)
            {
                header = new byte[8];
                header[0] = 0x82;
                header[1] = (byte)(0x80 | 126);
                header[2] = (byte)(payload.Length >> 8);
                header[3] = (byte)payload.Length;
            }
            else
            {
                header = new byte[14];
                header[0] = 0x82;
                header[1] = (byte)(0x80 | 127);

                long length = payload.Length;
                for (int i = 0; i < 8; i++) header[2 + i] = (byte)(length >> (8 * (7 - i)));
            }

            int maskOffset = header.Length - 4;
            Array.Copy(mask, 0, header, maskOffset, 4);

            byte[] masked = new byte[payload.Length];
            for (int i = 0; i < payload.Length; i++) masked[i] = (byte)(payload[i] ^ mask[i & 3]);

            stream.Write(header, 0, header.Length);
            stream.Write(masked, 0, masked.Length);
            stream.Flush();
        }

        private static byte[] ReadMessage(NetworkStream stream)
        {
            MemoryStream message = null;

            while (true)
            {
                byte[] header = ReadExact(stream, 2);
                if (header == null) return null;

                bool fin = (header[0] & 0x80) != 0;
                int opcode = header[0] & 0x0F;
                bool masked = (header[1] & 0x80) != 0;
                long length = header[1] & 0x7F;

                if (length == 126)
                {
                    byte[] ext = ReadExact(stream, 2);
                    if (ext == null) return null;
                    length = (ext[0] << 8) | ext[1];
                }
                else if (length == 127)
                {
                    byte[] ext = ReadExact(stream, 8);
                    if (ext == null) return null;

                    length = 0;
                    for (int i = 0; i < 8; i++) length = (length << 8) | ext[i];
                }

                byte[] mask = masked ? ReadExact(stream, 4) : null;
                byte[] payload = ReadExact(stream, (int)length);
                if (payload == null) return null;

                if (masked)
                    for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

                if (opcode == 0x8) return null;   // close
                if (opcode == 0x9 || opcode == 0xA) continue;

                if (message == null) message = new MemoryStream();
                message.Write(payload, 0, payload.Length);

                if (fin) return message.ToArray();
            }
        }

        private static byte[] ReadExact(NetworkStream stream, int count)
        {
            if (count == 0) return new byte[0];

            byte[] buffer = new byte[count];
            int read = 0;

            while (read < count)
            {
                int chunk = stream.Read(buffer, read, count - read);
                if (chunk <= 0) return null;
                read += chunk;
            }

            return buffer;
        }
    }
}