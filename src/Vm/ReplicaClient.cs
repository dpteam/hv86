using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Hv86.Core;

namespace Hv86.Vm
{
    /// <summary>
    /// Отправка состояния на реплику (основной хост).
    ///
    /// Базовый образ уезжает один раз на эпоху (при старте ВМ), дальше летает
    /// только state. Все запросы идемпотентны по смыслу: повтор после обрыва
    /// либо перезапишет образ той же эпохи, либо получит duplicate по seq.
    /// </summary>
    public static class ReplicaClient
    {
        private static readonly HttpClient Http = CreateClient();

        private static HttpClient CreateClient()
        {
            HttpClient client = new HttpClient();
            client.Timeout = TimeSpan.FromMinutes(10);

            // 100-continue на больших телах только мешает: сервер и так знает размер
            client.DefaultRequestHeaders.ExpectContinue = false;
            return client;
        }

        /// <summary>Нормализует адрес реплики: без хвостового слеша, с префиксом.</summary>
        public static string NormalizeUrl(string url)
        {
            if (string.IsNullOrEmpty(url)) throw new Hv86Exception("не задан адрес реплики");

            string text = url.Trim().TrimEnd('/');

            if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                text = "http://" + text;

            if (text.IndexOf(ReplicaServer.PathPrefix.Trim('/'), StringComparison.OrdinalIgnoreCase) < 0)
                text += ReplicaServer.PathPrefix.TrimEnd('/');

            return text + "/";
        }

        public static async Task<Dictionary<string, object>> HelloAsync(string url, string token, int timeoutMs)
        {
            return await SendAsync(HttpMethod.Get, url + "hello", token, null, timeoutMs, null)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Новая эпоха: отправляет базовый образ целиком. Возвращает meta реплики.
        /// </summary>
        public static async Task<Dictionary<string, object>> BeginAsync(string url, string token,
            VmDefinition vm, string diskPath, int epoch, int timeoutMs)
        {
            if (!File.Exists(diskPath)) throw new Hv86Exception("образ диска не найден: " + diskPath);

            FileInfo info = new FileInfo(diskPath);

            string query = "?vm=" + Uri.EscapeDataString(vm.Id) +
                           "&epoch=" + epoch +
                           "&disk_size=" + info.Length +
                           "&disk_sha256=" + AtomicFile.Sha256(diskPath) +
                           "&name=" + Uri.EscapeDataString(vm.Name ?? "") +
                           "&vm_json=" + Uri.EscapeDataString(Json.Stringify(vm.ToDict()));

            using (FileStream body = new FileStream(diskPath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan))
            {
                return await SendAsync(HttpMethod.Post, url + "begin" + query, token, body, timeoutMs,
                    info.Length).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Отправляет state. seq растёт на каждом чекпоинте; повтор с тем же seq
        /// реплика считает дубликатом и не портит себе состояние.
        /// </summary>
        public static async Task<Dictionary<string, object>> PutStateAsync(string url, string token,
            string vmId, int epoch, long seq, string statePath, int timeoutMs, int relayPort = 0,
            string lease = null)
        {
            if (!File.Exists(statePath)) throw new Hv86Exception("state не найден: " + statePath);

            long size = new FileInfo(statePath).Length;

            string query = "?vm=" + Uri.EscapeDataString(vmId) +
                           "&epoch=" + epoch +
                           "&seq=" + seq +
                           "&size=" + size +
                           (relayPort > 0 ? "&relay_port=" + relayPort : "") +
                           (string.IsNullOrEmpty(lease) ? "" : "&lease=" + Uri.EscapeDataString(lease));

            using (FileStream body = new FileStream(statePath, FileMode.Open, FileAccess.Read,
                       FileShare.ReadWrite, 1 << 16, FileOptions.SequentialScan))
            {
                return await SendAsync(HttpMethod.Put, url + "state" + query, token, body, timeoutMs, size)
                    .ConfigureAwait(false);
            }
        }

        public static async Task<Dictionary<string, object>> StatusAsync(string url, string token,
            string vmId, int timeoutMs)
        {
            return await SendAsync(HttpMethod.Get,
                url + "status?vm=" + Uri.EscapeDataString(vmId), token, null, timeoutMs, null)
                .ConfigureAwait(false);
        }

        private static async Task<Dictionary<string, object>> SendAsync(HttpMethod method, string url,
            string token, Stream body, int timeoutMs, long? length)
        {
            using (HttpRequestMessage request = new HttpRequestMessage(method, url))
            {
                request.Headers.TryAddWithoutValidation("X-Hv86-Token", token ?? "");

                if (body != null)
                {
                    // StreamContent без seek: FileStream его умеет; длина нужна, чтобы
                    // сервер не гадал и не включал chunked
                    StreamContent content = new StreamContent(body);
                    content.Headers.ContentLength = length;
                    content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/octet-stream");

                    request.Content = content;
                }

                using (System.Threading.CancellationTokenSource cancel =
                       new System.Threading.CancellationTokenSource(Math.Max(1000, timeoutMs)))
                {
                    HttpResponseMessage response;

                    try
                    {
                        response = await Http.SendAsync(request, cancel.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        throw new Hv86Exception("реплика не ответила за " + (timeoutMs / 1000) + " с");
                    }
                    catch (HttpRequestException ex)
                    {
                        throw new Hv86Exception("реплика недоступна (" + url + "): " + ex.Message);
                    }

                    using (response)
                    {
                        string text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                        Dictionary<string, object> reply = Json.ParseObjectSafe(text);

                        if (!response.IsSuccessStatusCode)
                        {
                            string error = Json.GetString(reply, "error", text);

                            throw new Hv86Exception("реплика ответила " + (int)response.StatusCode +
                                                    ": " + error);
                        }

                        return reply ?? Json.Obj();
                    }
                }
            }
        }
    }
}