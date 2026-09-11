using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace CmdBridgeClient
{
    /// <summary>
    /// CmdBridge 协议客户端：定位游戏进程 → 读运行时发现文件 → 一行 JSON 一次请求。
    /// 纯 BCL 实现，不引用任何游戏程序集。
    /// </summary>
    internal sealed class BridgeClient : IDisposable
    {
        private const string RuntimeFileName = "CmdBridge.runtime.json";

        private TcpClient m_client;
        private NetworkStream m_stream;
        private int m_requestCounter;

        private BridgeClient(string host, int port, string token, string instanceRoot)
        {
            Host = host;
            Port = port;
            Token = token;
            InstanceRoot = instanceRoot;
        }

        public string Host { get; }

        public int Port { get; }

        public string Token { get; }

        public string InstanceRoot { get; }

        public static BridgeClient Connect(string explicitRoot, int? explicitPort, string explicitToken)
        {
            string root = explicitRoot;
            if (string.IsNullOrEmpty(root))
                root = FindGameDirectory();

            int port = explicitPort ?? 0;
            string token = explicitToken;

            if (string.IsNullOrEmpty(root))
            {
                if (port <= 0 || string.IsNullOrEmpty(token))
                {
                    throw new BridgeException(
                        "discovery_failed",
                        "Survivalcraft is not running and --root was not supplied.");
                }
            }
            else
            {
                string path = Path.Combine(root, RuntimeFileName);
                if (!File.Exists(path))
                {
                    if (port <= 0 || string.IsNullOrEmpty(token))
                    {
                        throw new BridgeException(
                            "runtime_file_missing",
                            "CmdBridge.runtime.json was not found in " + root +
                            " (is CmdBridgeMod loaded?).");
                    }
                }
                else
                {
                    using (JsonDocument document = JsonDocument.Parse(File.ReadAllText(path, Encoding.UTF8)))
                    {
                        JsonElement json = document.RootElement;
                        if (port <= 0 && json.TryGetProperty("port", out JsonElement portElement))
                            port = portElement.GetInt32();
                        if (string.IsNullOrEmpty(token) &&
                            json.TryGetProperty("token", out JsonElement tokenElement))
                        {
                            token = tokenElement.GetString();
                        }
                    }
                }
            }

            if (port <= 0)
                throw new BridgeException("discovery_failed", "No CmdBridge port was discovered.");
            if (string.IsNullOrEmpty(token))
                throw new BridgeException("discovery_failed", "No CmdBridge token was discovered.");

            return new BridgeClient("127.0.0.1", port, token, root);
        }

        private static string FindGameDirectory()
        {
            try
            {
                Process[] processes = Process.GetProcessesByName("Survivalcraft");
                for (int i = 0; i < processes.Length; i++)
                {
                    try
                    {
                        string fileName = processes[i].MainModule.FileName;
                        if (!string.IsNullOrEmpty(fileName))
                            return Path.GetDirectoryName(fileName);
                    }
                    catch
                    {
                    }
                    finally
                    {
                        processes[i].Dispose();
                    }
                }
            }
            catch
            {
            }
            return null;
        }

        public JsonElement Send(string command, Dictionary<string, object> arguments, int timeoutMs)
        {
            EnsureConnected(timeoutMs);
            if (m_stream != null)
                m_stream.ReadTimeout = timeoutMs;   // 每次请求都可单独放宽（waitFor 会久等）

            var request = new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["id"] = (++m_requestCounter).ToString(),
                ["token"] = Token,
                ["command"] = command
            };
            if (arguments != null && arguments.Count > 0)
                request["args"] = arguments;

            string payload = JsonSerializer.Serialize(request) + "\n";
            byte[] bytes = Encoding.UTF8.GetBytes(payload);
            m_stream.Write(bytes, 0, bytes.Length);
            m_stream.Flush();

            string line = ReadLine(timeoutMs);
            if (line == null)
                throw new BridgeException("connection_closed", "The game closed the connection.");

            using (JsonDocument document = JsonDocument.Parse(line))
            {
                JsonElement root = document.RootElement;
                if (root.TryGetProperty("ok", out JsonElement ok) && ok.GetBoolean())
                {
                    return root.TryGetProperty("result", out JsonElement result)
                        ? result.Clone()
                        : default;
                }

                string code = "command_failed";
                string message = "The game reported an error.";
                if (root.TryGetProperty("error", out JsonElement error))
                {
                    if (error.TryGetProperty("code", out JsonElement codeElement))
                        code = codeElement.GetString();
                    if (error.TryGetProperty("message", out JsonElement messageElement))
                        message = messageElement.GetString();
                }
                throw new BridgeException(code, message);
            }
        }

        private void EnsureConnected(int timeoutMs)
        {
            if (m_client != null && m_client.Connected)
                return;

            m_client = new TcpClient();
            m_client.NoDelay = true;
            IAsyncResult pending = m_client.BeginConnect(Host, Port, null, null);
            if (!pending.AsyncWaitHandle.WaitOne(timeoutMs))
            {
                m_client.Close();
                m_client = null;
                throw new BridgeException(
                    "connect_timeout",
                    "Timed out connecting to " + Host + ":" + Port + ".");
            }
            m_client.EndConnect(pending);
            m_stream = m_client.GetStream();
            m_stream.ReadTimeout = timeoutMs;
            m_stream.WriteTimeout = timeoutMs;
        }

        private string ReadLine(int timeoutMs)
        {
            var buffer = new MemoryStream();
            var single = new byte[1];
            while (true)
            {
                int read;
                try
                {
                    read = m_stream.Read(single, 0, 1);
                }
                catch (IOException)
                {
                    return null;
                }
                if (read <= 0)
                    return null;
                if (single[0] == (byte)'\n')
                    return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                if (single[0] != (byte)'\r')
                    buffer.WriteByte(single[0]);
                if (buffer.Length > 8 * 1024 * 1024)
                    throw new BridgeException("response_too_large", "Response exceeded 8 MiB.");
            }
        }

        public void Dispose()
        {
            try
            {
                m_stream?.Dispose();
            }
            catch
            {
            }
            try
            {
                m_client?.Close();
            }
            catch
            {
            }
            m_stream = null;
            m_client = null;
        }
    }

    internal sealed class BridgeException : Exception
    {
        public BridgeException(string code, string message)
            : base(message)
        {
            Code = code;
        }

        public string Code { get; }
    }
}
