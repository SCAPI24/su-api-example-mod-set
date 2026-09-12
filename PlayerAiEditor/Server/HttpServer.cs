using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace PlayerAiMod.Editor
{
    /// <summary>一次 HTTP 请求（够用即可：路径、方法、查询、正文）。</summary>
    internal sealed class HttpRequestInfo
    {
        public string Method;
        public string Path;
        public string RawQuery;
        public readonly Dictionary<string, string> Query =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];

        public string BodyText
        {
            get { return Body == null || Body.Length == 0 ? string.Empty : Encoding.UTF8.GetString(Body); }
        }

        public string GetQuery(string name, string fallback = null)
        {
            string value;
            return Query.TryGetValue(name, out value) && !string.IsNullOrEmpty(value) ? value : fallback;
        }
    }

    /// <summary>
    /// 极简 HTTP/1.1 服务器（TcpListener 实现）。
    ///
    /// 为什么不用 `HttpListener`：它在本机非管理员账号下需要 URL ACL（`netsh http add urlacl`），
    /// 而"打开编辑器还要先配权限"对用户不友好。自己解析请求头几十行就够，而且和 CmdBridge 的
    /// 控制通道一样是纯 TcpListener —— 任何端口都能绑，不需要提权。
    ///
    /// 只支持编辑器需要的子集：GET/POST、Content-Length 正文、Connection: close。
    /// </summary>
    internal sealed class HttpServer : IDisposable
    {
        public const int MaximumBodyBytes = 32 * 1024 * 1024;
        public const int MaximumHeaderBytes = 64 * 1024;

        private readonly Func<HttpRequestInfo, HttpResponse> m_handler;
        private readonly TcpListener m_listener;
        private volatile bool m_stopping;

        public HttpServer(int port, Func<HttpRequestInfo, HttpResponse> handler)
        {
            m_handler = handler;
            m_listener = new TcpListener(IPAddress.Loopback, port);
        }

        public int Port
        {
            get { return ((IPEndPoint)m_listener.LocalEndpoint).Port; }
        }

        public void Start()
        {
            m_listener.Start();
            var thread = new Thread(AcceptLoop) { IsBackground = true, Name = "PlayerAiEditor.Http" };
            thread.Start();
        }

        private void AcceptLoop()
        {
            while (!m_stopping)
            {
                TcpClient client = null;
                try
                {
                    client = m_listener.AcceptTcpClient();
                }
                catch (Exception)
                {
                    if (m_stopping)
                        return;
                    continue;
                }

                try
                {
                    Handle(client);
                }
                catch (Exception exception)
                {
                    Console.WriteLine("[editor] request failed: " + exception.GetType().Name
                        + ": " + exception.Message);
                }
                finally
                {
                    try
                    {
                        client.Close();
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        private void Handle(TcpClient client)
        {
            client.ReceiveTimeout = 15000;
            client.SendTimeout = 15000;
            using (NetworkStream stream = client.GetStream())
            {
                HttpRequestInfo request = ReadRequest(stream);
                HttpResponse response = request == null
                    ? HttpResponse.Text(400, "bad request")
                    : (m_handler != null ? m_handler(request) : HttpResponse.Text(404, "no handler"));
                WriteResponse(stream, response);
            }
        }

        private static HttpRequestInfo ReadRequest(NetworkStream stream)
        {
            var headerBytes = new List<byte>(1024);
            var one = new byte[1];
            while (headerBytes.Count < MaximumHeaderBytes)
            {
                int read = stream.Read(one, 0, 1);
                if (read <= 0)
                    return null;
                headerBytes.Add(one[0]);
                int count = headerBytes.Count;
                if (count >= 4 && headerBytes[count - 4] == 13 && headerBytes[count - 3] == 10
                    && headerBytes[count - 2] == 13 && headerBytes[count - 1] == 10)
                {
                    break;
                }
            }

            string headerText = Encoding.ASCII.GetString(headerBytes.ToArray());
            string[] lines = headerText.Split(new[] { "\r\n" }, StringSplitOptions.None);
            if (lines.Length == 0)
                return null;

            string[] parts = lines[0].Split(' ');
            if (parts.Length < 2)
                return null;

            var request = new HttpRequestInfo { Method = parts[0].ToUpperInvariant() };
            string target = parts[1];
            int question = target.IndexOf('?');
            if (question >= 0)
            {
                request.Path = Uri.UnescapeDataString(target.Substring(0, question));
                request.RawQuery = target.Substring(question + 1);
                foreach (string pair in request.RawQuery.Split('&'))
                {
                    if (pair.Length == 0)
                        continue;
                    int equals = pair.IndexOf('=');
                    if (equals < 0)
                        request.Query[Uri.UnescapeDataString(pair)] = string.Empty;
                    else
                        request.Query[Uri.UnescapeDataString(pair.Substring(0, equals))] =
                            Uri.UnescapeDataString(pair.Substring(equals + 1).Replace('+', ' '));
                }
            }
            else
            {
                request.Path = Uri.UnescapeDataString(target);
                request.RawQuery = string.Empty;
            }

            int contentLength = 0;
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                int colon = line.IndexOf(':');
                if (colon <= 0)
                    continue;
                string name = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (string.Equals(name, "Content-Length", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(value, out contentLength);
            }

            if (contentLength > 0)
            {
                if (contentLength > MaximumBodyBytes)
                    throw new InvalidDataException("request body too large");
                var body = new byte[contentLength];
                int offset = 0;
                while (offset < contentLength)
                {
                    int read = stream.Read(body, offset, contentLength - offset);
                    if (read <= 0)
                        break;
                    offset += read;
                }
                request.Body = body;
            }
            return request;
        }

        private static void WriteResponse(NetworkStream stream, HttpResponse response)
        {
            if (response == null)
                response = HttpResponse.Text(500, "no response");

            byte[] body = response.Body ?? new byte[0];
            var header = new StringBuilder();
            header.Append("HTTP/1.1 ").Append(response.Status).Append(' ')
                .Append(Reason(response.Status)).Append("\r\n");
            header.Append("Content-Type: ").Append(response.ContentType ?? "application/octet-stream")
                .Append("\r\n");
            header.Append("Content-Length: ").Append(body.Length).Append("\r\n");
            header.Append("Cache-Control: no-store\r\n");
            header.Append("Connection: close\r\n");
            header.Append("\r\n");

            byte[] headerBytes = Encoding.ASCII.GetBytes(header.ToString());
            stream.Write(headerBytes, 0, headerBytes.Length);
            if (body.Length > 0)
                stream.Write(body, 0, body.Length);
            stream.Flush();
        }

        private static string Reason(int status)
        {
            switch (status)
            {
                case 200: return "OK";
                case 201: return "Created";
                case 204: return "No Content";
                case 400: return "Bad Request";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 409: return "Conflict";
                case 413: return "Payload Too Large";
                case 500: return "Internal Server Error";
                default: return "Status";
            }
        }

        public void Dispose()
        {
            m_stopping = true;
            try
            {
                m_listener.Stop();
            }
            catch (Exception)
            {
            }
        }
    }

    /// <summary>一次 HTTP 响应。</summary>
    internal sealed class HttpResponse
    {
        public int Status = 200;
        public string ContentType = "application/json; charset=utf-8";
        public byte[] Body = new byte[0];

        public static HttpResponse Json(object value, int status = 200)
        {
            return new HttpResponse
            {
                Status = status,
                ContentType = "application/json; charset=utf-8",
                Body = Encoding.UTF8.GetBytes(JsonWriter.Write(value))
            };
        }

        public static HttpResponse Text(int status, string text, string contentType = "text/plain; charset=utf-8")
        {
            return new HttpResponse
            {
                Status = status,
                ContentType = contentType,
                Body = Encoding.UTF8.GetBytes(text ?? string.Empty)
            };
        }

        public static HttpResponse Html(string html)
        {
            return Text(200, html, "text/html; charset=utf-8");
        }
    }
}
