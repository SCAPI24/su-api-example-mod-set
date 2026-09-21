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
            int port = explicitPort ?? 0;
            string token = explicitToken;
            // 只在"缺端口或 token"时才需要定位实例目录：显式给了 --port/--token（例如经 adb forward
            // 连 Android 上的游戏）就不该因为本机跑着别的实例而报 instance_ambiguous。
            if (string.IsNullOrEmpty(root) && (port <= 0 || string.IsNullOrEmpty(token)))
                root = ResolveSingleInstanceRoot();

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

        /// <summary>一个正在运行的游戏实例（进程 + 它的发现文件）。</summary>
        private sealed class RunningInstance
        {
            public int ProcessId;

            public string Root;

            public int Port;

            public string InstanceId;

            public bool RuntimeIsFresh;
        }

        /// <summary>
        /// 同一台机器可以同时运行多个游戏实例（每个实例有自己的目录与发现文件）。
        /// 没有 --root 时**不能猜**：0 个 → 交给调用方报"未运行"；
        /// 1 个 → 用它；多个 → 报歧义并列出候选，让调用方用 --root 指定。
        /// Source: Mod/CmdBridgeMod/Server/CmdBridgeConfig.cs:CmdBridgeRuntime（pid/port 写入发现文件）
        /// </summary>
        private static string ResolveSingleInstanceRoot()
        {
            List<RunningInstance> instances = EnumerateInstances();
            if (instances.Count == 0)
                return null;
            if (instances.Count == 1)
                return instances[0].Root;

            var builder = new StringBuilder();
            for (int i = 0; i < instances.Count; i++)
            {
                if (i > 0)
                    builder.Append("; ");
                builder.Append(instances[i].Root).Append(" (pid=").Append(instances[i].ProcessId);
                if (!string.IsNullOrEmpty(instances[i].InstanceId))
                    builder.Append(", id=").Append(instances[i].InstanceId);
                builder.Append(instances[i].RuntimeIsFresh
                    ? ", port=" + instances[i].Port
                    : ", no fresh runtime file");
                builder.Append(')');
            }
            throw new BridgeException("instance_ambiguous",
                "Multiple Survivalcraft instances are running: " + builder +
                ". Pass --root <game dir> to choose one.");
        }

        /// <summary>枚举运行中的实例；发现文件里的 pid 必须与进程一致，陈旧文件会被忽略。</summary>
        private static List<RunningInstance> EnumerateInstances()
        {
            var instances = new List<RunningInstance>();
            var seenRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Process[] processes;
            try
            {
                processes = Process.GetProcessesByName("Survivalcraft");
            }
            catch
            {
                return instances;
            }
            for (int i = 0; i < processes.Length; i++)
            {
                try
                {
                    string fileName = processes[i].MainModule.FileName;
                    if (string.IsNullOrEmpty(fileName))
                        continue;
                    string root = Path.GetDirectoryName(fileName);
                    if (string.IsNullOrEmpty(root) || !seenRoots.Add(root))
                        continue;

                    var instance = new RunningInstance
                    {
                        ProcessId = processes[i].Id,
                        Root = root
                    };
                    string path = Path.Combine(root, RuntimeFileName);
                    if (File.Exists(path))
                    {
                        using (JsonDocument document = JsonDocument.Parse(
                            File.ReadAllText(path, Encoding.UTF8)))
                        {
                            JsonElement json = document.RootElement;
                            int runtimePid = json.TryGetProperty("pid", out JsonElement pidElement)
                                ? pidElement.GetInt32()
                                : 0;
                            // 陈旧（上一次被杀掉留下的）发现文件不算数：pid 必须就是本进程。
                            instance.RuntimeIsFresh = runtimePid == processes[i].Id;
                            if (instance.RuntimeIsFresh)
                            {
                                if (json.TryGetProperty("port", out JsonElement portElement))
                                    instance.Port = portElement.GetInt32();
                                if (json.TryGetProperty("instanceId", out JsonElement idElement))
                                    instance.InstanceId = idElement.GetString();
                            }
                        }
                    }
                    instances.Add(instance);
                }
                catch
                {
                }
                finally
                {
                    processes[i].Dispose();
                }
            }
            return instances;
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
