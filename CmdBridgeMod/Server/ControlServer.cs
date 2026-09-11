using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace CmdBridgeMod
{
    /// <summary>
    /// 本机 TCP JSON 控制服务：只监听数值 loopback 地址 + token 鉴权。
    /// 一个请求一行 UTF-8 JSON，一个响应一行 JSON。
    /// Source: Mod/HeadlessRenderingMod/Server/HeadlessControlServer.cs（同形态）
    /// </summary>
    internal sealed class ControlServer
    {
        private const int MaxConnections = 8;

        private readonly CmdBridgeConfig m_config;
        private readonly CommandRouter m_router;
        private readonly SemaphoreSlim m_connectionSlots =
            new SemaphoreSlim(MaxConnections, MaxConnections);
        private readonly object m_clientsLock = new object();
        private readonly HashSet<TcpClient> m_clients = new HashSet<TcpClient>();

        private TcpListener m_listener;
        private Thread m_acceptThread;
        private volatile bool m_running;
        private volatile string m_lastError;

        public ControlServer(CmdBridgeConfig config, CommandRouter router)
        {
            m_config = config;
            m_router = router;
        }

        public string LastError => m_lastError;

        public bool IsRunning => m_running;

        public void Start()
        {
            if (m_running)
                throw new InvalidOperationException("Control server is already running.");

            IPAddress address = IPAddress.Parse(m_config.BindAddress);
            m_listener = new TcpListener(address, m_config.Port);
            m_listener.Start(MaxConnections);
            m_running = true;
            m_acceptThread = new Thread(AcceptLoop)
            {
                IsBackground = true,
                Name = "CmdBridgeMod.Accept"
            };
            m_acceptThread.Start();
        }

        public void Stop()
        {
            if (!m_running)
                return;
            m_running = false;
            try
            {
                m_listener?.Stop();
            }
            catch
            {
            }

            TcpClient[] clients;
            lock (m_clientsLock)
            {
                clients = new TcpClient[m_clients.Count];
                m_clients.CopyTo(clients);
            }
            for (int i = 0; i < clients.Length; i++)
            {
                try
                {
                    clients[i].Close();
                }
                catch
                {
                }
            }

            m_acceptThread?.Join(1000);
            m_acceptThread = null;
            m_listener = null;
        }

        private void AcceptLoop()
        {
            while (m_running)
            {
                TcpClient client = null;
                try
                {
                    client = m_listener.AcceptTcpClient();
                    client.NoDelay = true;
                    if (!m_connectionSlots.Wait(0))
                    {
                        using (client)
                        using (NetworkStream busyStream = client.GetStream())
                        {
                            WriteResponse(busyStream, WireResponse.Error(
                                null, "server_busy", "Too many connections."));
                        }
                        continue;
                    }

                    lock (m_clientsLock)
                        m_clients.Add(client);

                    TcpClient accepted = client;
                    var thread = new Thread(() => HandleClient(accepted))
                    {
                        IsBackground = true,
                        Name = "CmdBridgeMod.Client"
                    };
                    thread.Start();
                    client = null;
                }
                catch (SocketException exception)
                {
                    if (m_running)
                        m_lastError = exception.Message;
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception exception)
                {
                    if (m_running)
                        m_lastError = exception.Message;
                }
                finally
                {
                    client?.Close();
                }
            }
        }

        private void HandleClient(TcpClient client)
        {
            try
            {
                client.ReceiveTimeout = m_config.RequestTimeoutSeconds * 4000;
                client.SendTimeout = m_config.RequestTimeoutSeconds * 4000;
                using (client)
                using (NetworkStream stream = client.GetStream())
                {
                    while (m_running)
                    {
                        string line = ReadLimitedLine(stream, m_config.MaxRequestBytes);
                        if (line == null)
                            break;
                        if (line.Length == 0)
                            continue;
                        WriteResponse(stream, HandleLine(line));
                    }
                }
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                lock (m_clientsLock)
                    m_clients.Remove(client);
                m_connectionSlots.Release();
            }
        }

        private Dictionary<string, object> HandleLine(string line)
        {
            BridgeRequest request;
            try
            {
                request = BridgeRequest.Parse(line);
            }
            catch (Exception exception) when (
                exception is JsonException || exception is InvalidDataException)
            {
                return WireResponse.Error(null, "invalid_request", exception.Message);
            }

            if (!FixedTimeEquals(request.Token, m_config.Token))
                return WireResponse.Error(request.Id, "unauthorized", "Invalid token.");

            try
            {
                return WireResponse.Success(request.Id, m_router.Execute(request));
            }
            catch (BridgeCommandException exception)
            {
                return WireResponse.Error(request.Id, exception.Code, exception.Message);
            }
            catch (TimeoutException exception)
            {
                return WireResponse.Error(request.Id, "timeout", exception.Message);
            }
            catch (Exception exception)
            {
                return WireResponse.Error(
                    request.Id,
                    "command_failed",
                    exception.GetType().Name + ": " + exception.Message);
            }
        }

        private static string ReadLimitedLine(NetworkStream stream, int maximumBytes)
        {
            using (var buffer = new MemoryStream(Math.Min(maximumBytes, 4096)))
            {
                while (true)
                {
                    int value = stream.ReadByte();
                    if (value < 0)
                    {
                        if (buffer.Length == 0)
                            return null;
                        throw new EndOfStreamException();
                    }
                    if (value == '\n')
                        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                    if (value == '\r')
                        continue;
                    if (buffer.Length >= maximumBytes)
                        throw new InvalidDataException("Request line is too large.");
                    buffer.WriteByte((byte)value);
                }
            }
        }

        private static void WriteResponse(
            NetworkStream stream, Dictionary<string, object> response)
        {
            string json = JsonSerializer.Serialize(response) + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(json);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        private static bool FixedTimeEquals(string left, string right)
        {
            if (left == null || right == null || left.Length != right.Length)
                return false;
            int difference = 0;
            for (int i = 0; i < left.Length; i++)
                difference |= left[i] ^ right[i];
            return difference == 0;
        }
    }

    internal sealed class BridgeRequest
    {
        private BridgeRequest(string id, string command, string token, JsonElement payload)
        {
            Id = id;
            Command = command;
            Token = token;
            Payload = payload;
        }

        public string Id { get; }

        public string Command { get; }

        public string Token { get; }

        private JsonElement Payload { get; }

        public static BridgeRequest Parse(string json)
        {
            using (JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            }))
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    throw new InvalidDataException("Request root must be a JSON object.");

                string id = ReadRequiredString(root, "id", 128, true);
                string command = ReadRequiredString(root, "command", 64, false);
                string token = ReadRequiredString(root, "token", 256, false);
                return new BridgeRequest(id, command.ToLowerInvariant(), token, root.Clone());
            }
        }

        public bool TryGetString(string name, out string value)
        {
            value = null;
            if (!TryGetArgument(name, out JsonElement element) ||
                element.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            value = element.GetString();
            return true;
        }

        public string GetString(string name, string fallback)
        {
            return TryGetString(name, out string value) ? value : fallback;
        }

        public bool TryGetInteger(string name, out int value)
        {
            value = 0;
            if (!TryGetArgument(name, out JsonElement element))
                return false;
            if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value))
                return true;
            if (element.ValueKind == JsonValueKind.String &&
                int.TryParse(element.GetString(), NumberStyles.Integer,
                    CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
            throw new InvalidDataException(name + " must be an integer.");
        }

        public int GetInteger(string name, int fallback)
        {
            return TryGetInteger(name, out int value) ? value : fallback;
        }

        public bool TryGetFloat(string name, out float value)
        {
            value = 0f;
            if (!TryGetArgument(name, out JsonElement element))
                return false;
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double number))
            {
                value = (float)number;
                return true;
            }
            if (element.ValueKind == JsonValueKind.String &&
                float.TryParse(element.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out value))
            {
                return true;
            }
            throw new InvalidDataException(name + " must be a number.");
        }

        public float GetFloat(string name, float fallback)
        {
            if (!TryGetArgument(name, out JsonElement element))
                return fallback;
            if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out double number))
                return (float)number;
            if (element.ValueKind == JsonValueKind.String &&
                float.TryParse(element.GetString(), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out float parsed))
            {
                return parsed;
            }
            throw new InvalidDataException(name + " must be a number.");
        }

        public bool TryGetBoolean(string name, out bool value)
        {
            value = false;
            if (!TryGetArgument(name, out JsonElement element))
                return false;
            if (element.ValueKind == JsonValueKind.True || element.ValueKind == JsonValueKind.False)
            {
                value = element.GetBoolean();
                return true;
            }
            if (element.ValueKind == JsonValueKind.String &&
                bool.TryParse(element.GetString(), out bool parsed))
            {
                value = parsed;
                return true;
            }
            throw new InvalidDataException(name + " must be a boolean.");
        }

        public bool GetBoolean(string name, bool fallback)
        {
            return TryGetBoolean(name, out bool value) ? value : fallback;
        }

        public List<string> GetStringArray(string name)
        {
            var result = new List<string>();
            if (!TryGetArgument(name, out JsonElement element))
                return result;
            if (element.ValueKind == JsonValueKind.String)
            {
                result.Add(element.GetString());
                return result;
            }
            if (element.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException(name + " must be a string or an array of strings.");
            foreach (JsonElement item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException(name + " must contain only strings.");
                result.Add(item.GetString());
            }
            return result;
        }

        private bool TryGetArgument(string name, out JsonElement value)
        {
            if (Payload.TryGetProperty("args", out JsonElement args) &&
                args.ValueKind == JsonValueKind.Object &&
                args.TryGetProperty(name, out value))
            {
                return true;
            }
            return Payload.TryGetProperty(name, out value);
        }

        private static string ReadRequiredString(
            JsonElement root, string name, int maximumLength, bool allowNumber)
        {
            if (!root.TryGetProperty(name, out JsonElement element))
                throw new InvalidDataException("Request requires '" + name + "'.");

            string value;
            if (element.ValueKind == JsonValueKind.String)
                value = element.GetString();
            else if (allowNumber && element.ValueKind == JsonValueKind.Number)
                value = element.GetRawText();
            else
                throw new InvalidDataException(name + " must be a string.");

            if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
                throw new InvalidDataException(name + " has an invalid length.");
            return value;
        }
    }

    internal static class WireResponse
    {
        public static Dictionary<string, object> Success(string id, object result)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["ok"] = true,
                ["result"] = result
            };
        }

        public static Dictionary<string, object> Error(string id, string code, string message)
        {
            return new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["id"] = id,
                ["ok"] = false,
                ["error"] = new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["code"] = code,
                    ["message"] = message
                }
            };
        }
    }
}
