using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HeadlessRenderingMod
{
    internal sealed class HeadlessControlServer
    {
        private const int MaxConnections = 16;
        private static readonly JsonSerializerOptions s_jsonOptions =
            new JsonSerializerOptions { WriteIndented = false };

        private readonly HeadlessServerConfig m_config;
        private readonly ConcurrentQueue<PendingCommand> m_commands =
            new ConcurrentQueue<PendingCommand>();
        private readonly SemaphoreSlim m_connectionSlots =
            new SemaphoreSlim(MaxConnections, MaxConnections);
        private readonly object m_clientsLock = new object();
        private readonly HashSet<TcpClient> m_clients = new HashSet<TcpClient>();
        private TcpListener m_listener;
        private Thread m_acceptThread;
        private volatile bool m_running;
        private volatile string m_lastError;
        private int m_queuedCommandCount;

        public HeadlessControlServer(HeadlessServerConfig config)
        {
            m_config = config ?? throw new ArgumentNullException(nameof(config));
        }

        public int QueuedCommandCount => Volatile.Read(ref m_queuedCommandCount);

        public string LastError => m_lastError;

        public Dictionary<string, object> SubmitLocal(
            string command,
            Dictionary<string, object> arguments = null)
        {
            if (string.IsNullOrWhiteSpace(command))
                throw new ArgumentException("Command cannot be empty.", nameof(command));

            ControlRequest request = ControlRequest.CreateLocal(
                "console-" + Guid.NewGuid().ToString("N"),
                command,
                arguments);
            if (request.Command == "ping")
                return CreatePingResponse(request.Id);
            return QueueAndWait(request);
        }

        // Source: doc/plan/headless-server-mod.md:命令行控制
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
                Name = "HeadlessRenderingMod.Accept"
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

            while (m_commands.TryDequeue(out PendingCommand pending))
            {
                Interlocked.Decrement(ref m_queuedCommandCount);
                pending.Cancel();
            }
        }

        // Source: Survivalcraft/Game/Program.cs:Program.Run
        public void ProcessQueuedCommands(
            Func<ControlRequest, object> handler,
            int maximumCommands)
        {
            if (handler == null)
                throw new ArgumentNullException(nameof(handler));

            int processed = 0;
            while (processed < maximumCommands &&
                m_commands.TryDequeue(out PendingCommand pending))
            {
                processed++;
                Interlocked.Decrement(ref m_queuedCommandCount);
                if (!pending.TryStart())
                    continue;

                try
                {
                    object result = handler(pending.Request);
                    pending.Complete(WireResponse.Success(pending.Request.Id, result));
                }
                catch (ControlCommandException ex)
                {
                    pending.Complete(WireResponse.Error(
                        pending.Request.Id,
                        ex.Code,
                        ex.Message));
                }
                catch (Exception ex)
                {
                    pending.Complete(WireResponse.Error(
                        pending.Request.Id,
                        "command_failed",
                        ex.Message));
                }
            }
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
                        using (NetworkStream stream = client.GetStream())
                        {
                            WriteResponse(
                                stream,
                                WireResponse.Error(null, "server_busy", "Too many connections."));
                        }
                        continue;
                    }

                    lock (m_clientsLock)
                        m_clients.Add(client);

                    TcpClient acceptedClient = client;
                    Thread clientThread = new Thread(() => HandleClient(acceptedClient))
                    {
                        IsBackground = true,
                        Name = "HeadlessRenderingMod.Client"
                    };
                    clientThread.Start();
                    client = null;
                }
                catch (SocketException ex)
                {
                    if (m_running)
                        m_lastError = ex.Message;
                }
                catch (ObjectDisposedException)
                {
                }
                catch (Exception ex)
                {
                    if (m_running)
                        m_lastError = ex.Message;
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
                client.ReceiveTimeout = m_config.RequestTimeoutSeconds * 1000;
                client.SendTimeout = m_config.RequestTimeoutSeconds * 1000;
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

                        Dictionary<string, object> response = HandleRequest(line);
                        WriteResponse(stream, response);
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

        private Dictionary<string, object> HandleRequest(string line)
        {
            ControlRequest request;
            try
            {
                request = ControlRequest.Parse(line);
            }
            catch (Exception ex) when (
                ex is JsonException ||
                ex is InvalidDataException)
            {
                return WireResponse.Error(null, "invalid_request", ex.Message);
            }

            if (!FixedTimeEquals(request.Token, m_config.Token))
                return WireResponse.Error(request.Id, "unauthorized", "Invalid token.");

            if (request.Command == "ping")
                return CreatePingResponse(request.Id);

            return QueueAndWait(request);
        }

        private Dictionary<string, object> CreatePingResponse(string requestId)
        {
            return WireResponse.Success(
                requestId,
                new Dictionary<string, object>(StringComparer.Ordinal)
                {
                    ["pong"] = true,
                    ["instanceId"] = m_config.InstanceId
                });
        }

        private Dictionary<string, object> QueueAndWait(ControlRequest request)
        {
            int count = Interlocked.Increment(ref m_queuedCommandCount);
            if (count > m_config.MaxQueuedCommands)
            {
                Interlocked.Decrement(ref m_queuedCommandCount);
                return WireResponse.Error(
                    request.Id,
                    "queue_full",
                    "The game-thread command queue is full.");
            }

            PendingCommand pending = new PendingCommand(request);
            m_commands.Enqueue(pending);

            try
            {
                if (!pending.Completion.Task.Wait(
                    TimeSpan.FromSeconds(m_config.RequestTimeoutSeconds)))
                {
                    pending.Cancel();
                    return WireResponse.Error(
                        request.Id,
                        "timeout",
                        "The game thread did not process the command in time.");
                }
            }
            catch (AggregateException) when (pending.Completion.Task.IsCanceled)
            {
                return WireResponse.Error(
                    request.Id,
                    "server_stopping",
                    "The control server is stopping.");
            }

            return pending.Completion.Task.IsCompletedSuccessfully
                ? pending.Completion.Task.Result
                : WireResponse.Error(
                    request.Id,
                    "server_stopping",
                    "The control server is stopping.");
        }

        private static string ReadLimitedLine(NetworkStream stream, int maximumBytes)
        {
            using MemoryStream buffer = new MemoryStream(Math.Min(maximumBytes, 4096));
            while (true)
            {
                int value = stream.ReadByte();
                if (value < 0)
                    return buffer.Length == 0 ? null : throw new EndOfStreamException();
                if (value == '\n')
                    return Encoding.UTF8.GetString(
                        buffer.GetBuffer(),
                        0,
                        checked((int)buffer.Length));
                if (value == '\r')
                    continue;
                if (buffer.Length >= maximumBytes)
                    throw new InvalidDataException("Request line is too large.");
                buffer.WriteByte((byte)value);
            }
        }

        private static void WriteResponse(
            NetworkStream stream,
            Dictionary<string, object> response)
        {
            string json = JsonSerializer.Serialize(response, s_jsonOptions) + "\r\n";
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

    internal sealed class ControlRequest
    {
        private ControlRequest(
            string id,
            string command,
            string token,
            JsonElement payload)
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

        public static ControlRequest Parse(string json)
        {
            using JsonDocument document = JsonDocument.Parse(
                json,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });

            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Request root must be a JSON object.");

            string id = ReadIdentifier(root);
            string command = ReadRequiredString(root, "command", 64).ToLowerInvariant();
            string token = ReadRequiredString(root, "token", 256);
            return new ControlRequest(id, command, token, root.Clone());
        }

        public static ControlRequest CreateLocal(
            string id,
            string command,
            Dictionary<string, object> arguments)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>(
                StringComparer.Ordinal)
            {
                ["id"] = id,
                ["command"] = command
            };
            if (arguments != null && arguments.Count > 0)
                payload["args"] = arguments;

            JsonElement element = JsonSerializer.SerializeToElement(payload);
            return new ControlRequest(
                id,
                command.ToLowerInvariant(),
                null,
                element);
        }

        public static ControlRequest CreateInternal(
            string id,
            string command,
            JsonElement arguments)
        {
            Dictionary<string, object> payload = new Dictionary<string, object>(
                StringComparer.Ordinal)
            {
                ["id"] = id,
                ["command"] = command
            };
            if (arguments.ValueKind == JsonValueKind.Object)
                payload["args"] = arguments.Clone();

            JsonElement element = JsonSerializer.SerializeToElement(payload);
            return new ControlRequest(
                id,
                command.ToLowerInvariant(),
                null,
                element);
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

        public bool TryGetBoolean(string name, out bool value)
        {
            value = false;
            if (!TryGetArgument(name, out JsonElement element))
                return false;
            if (element.ValueKind == JsonValueKind.True ||
                element.ValueKind == JsonValueKind.False)
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
            throw new InvalidDataException($"{name} must be a boolean.");
        }

        public bool TryGetInteger(string name, out int value)
        {
            value = 0;
            if (!TryGetArgument(name, out JsonElement element))
                return false;
            if (element.ValueKind == JsonValueKind.Number &&
                element.TryGetInt32(out value))
            {
                return true;
            }
            if (element.ValueKind == JsonValueKind.String &&
                int.TryParse(element.GetString(), out value))
            {
                return true;
            }
            throw new InvalidDataException($"{name} must be an integer.");
        }

        public bool TryGetFloat(string name, out float value)
        {
            value = 0f;
            if (!TryGetArgument(name, out JsonElement element))
                return false;
            if (element.ValueKind == JsonValueKind.Number &&
                element.TryGetSingle(out value))
            {
                return true;
            }
            if (element.ValueKind == JsonValueKind.String &&
                float.TryParse(
                    element.GetString(),
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out value))
            {
                return true;
            }
            throw new InvalidDataException($"{name} must be a number.");
        }

        public bool TryGetElement(string name, out JsonElement value)
        {
            if (TryGetArgument(name, out JsonElement element))
            {
                value = element.Clone();
                return true;
            }
            value = default;
            return false;
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

        private static string ReadIdentifier(JsonElement root)
        {
            if (!root.TryGetProperty("id", out JsonElement value))
                throw new InvalidDataException("Request requires an 'id'.");

            string result;
            if (value.ValueKind == JsonValueKind.String)
                result = value.GetString();
            else if (value.ValueKind == JsonValueKind.Number)
                result = value.GetRawText();
            else
                throw new InvalidDataException("id must be a string or number.");

            if (string.IsNullOrWhiteSpace(result) || result.Length > 128)
                throw new InvalidDataException("id must contain 1-128 characters.");
            return result;
        }

        private static string ReadRequiredString(
            JsonElement root,
            string name,
            int maximumLength)
        {
            if (!TryReadString(root, name, out string value) ||
                string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException($"Request requires a non-empty '{name}'.");
            }
            if (value.Length > maximumLength)
                throw new InvalidDataException($"{name} is too long.");
            return value;
        }

        private static bool TryReadString(
            JsonElement root,
            string name,
            out string value)
        {
            value = null;
            if (!root.TryGetProperty(name, out JsonElement element) ||
                element.ValueKind != JsonValueKind.String)
            {
                return false;
            }
            value = element.GetString();
            return true;
        }
    }

    internal sealed class PendingCommand
    {
        private int m_state;

        public PendingCommand(ControlRequest request)
        {
            Request = request;
            Completion = new TaskCompletionSource<Dictionary<string, object>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }

        public ControlRequest Request { get; }

        public TaskCompletionSource<Dictionary<string, object>> Completion { get; }

        public bool TryStart()
        {
            return Interlocked.CompareExchange(ref m_state, 1, 0) == 0;
        }

        public void Complete(Dictionary<string, object> response)
        {
            if (Interlocked.CompareExchange(ref m_state, 2, 1) == 1)
                Completion.TrySetResult(response);
        }

        public void Cancel()
        {
            if (Interlocked.CompareExchange(ref m_state, 3, 0) == 0)
                Completion.TrySetCanceled();
        }
    }

    internal sealed class ControlCommandException : Exception
    {
        public ControlCommandException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
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

        public static Dictionary<string, object> Error(
            string id,
            string code,
            string message)
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
