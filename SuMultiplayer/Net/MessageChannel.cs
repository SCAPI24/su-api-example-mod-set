using System;
using System.Collections.Generic;
using Google.Protobuf;
using SuMultiplayer.Protocol;

namespace SuMultiplayer.Net
{
    /// <summary>
    /// 一条入站消息的上下文（与载荷一起交给 <see cref="IMessageHandler{T}"/>）：
    /// 来源连接、TCP 帧序号、帧类型与载荷字节数。**只读**，不含任何会随消息变化的状态。
    /// </summary>
    public readonly struct MessageContext
    {
        public MessageContext(int clientId, long sequence, MessageType type, int payloadBytes,
            byte[] payload = null)
        {
            ClientId = clientId;
            Sequence = sequence;
            Type = type;
            PayloadBytes = payloadBytes;
            Payload = payload;
        }

        /// <summary>发送方连接编号（主机侧 = 对端 clientId；客户端侧 = 0）。</summary>
        public int ClientId { get; }

        /// <summary>发送方在这条 TCP 连接上的帧序号（诊断 / 去重可用）。</summary>
        public long Sequence { get; }

        /// <summary>帧头里的紧凑类型 id。</summary>
        public MessageType Type { get; }

        public int PayloadBytes { get; }

        /// <summary>
        /// 原始载荷（只读）。只有 <see cref="MessageRegistry.RegisterRaw"/> 的处理器需要它：
        /// 强类型处理器请直接用解析好的消息，避免重复解析。
        /// </summary>
        public byte[] Payload { get; }
    }

    /// <summary>
    /// 强类型消息处理器。实现方只拿到已经解析好的 protobuf 消息；
    /// **不需要**自己再解析 byte[]，也不会被要求处理别的类型。
    /// </summary>
    public interface IMessageHandler<in T> where T : class, IMessage<T>
    {
        void Handle(in MessageContext context, T message);
    }

    /// <summary>用 lambda / 方法组注册处理器的轻量适配（只在注册时分配一次）。</summary>
    public delegate void MessageHandlerCallback<T>(MessageContext context, T message);

    /// <summary>把 <see cref="MessageHandlerCallback{T}"/> 包成 <see cref="IMessageHandler{T}"/>。</summary>
    public sealed class DelegateMessageHandler<T> : IMessageHandler<T> where T : class, IMessage<T>
    {
        private readonly MessageHandlerCallback<T> m_callback;

        public DelegateMessageHandler(MessageHandlerCallback<T> callback)
        {
            m_callback = callback ?? throw new ArgumentNullException(nameof(callback));
        }

        public void Handle(in MessageContext context, T message) => m_callback(context, message);
    }

    /// <summary>
    /// 第三方消息模块：实现它，把自己的 <c>.proto</c> 生成类型 + 处理器注册进
    /// <see cref="MessageRegistry"/>。组合根（或宿主）会在通道建立后调用一次 <see cref="Register"/>。
    /// 见 <c>doc/message-protocol.md</c> 的完整示例。
    /// </summary>
    public interface ISuMPMessageModule
    {
        /// <summary>稳定模块 id（用于注册归属、诊断与卸载；不参与线格式）。</summary>
        string ModuleId { get; }

        /// <summary>
        /// 注册消息：<c>registry.Register(1000, MyMessage.Parser, handler, ModuleId)</c>。
        /// 重复调用必须幂等（同一个 id / 同一个类型只允许一个模块注册）。
        /// </summary>
        void Register(MessageRegistry registry);
    }

    /// <summary>
    /// 发消息的最小表面：<c>Send(clientId, message)</c>。
    /// <list type="bullet">
    /// <item>主机侧：<paramref name="clientId"/> &gt; 0 只发给那个玩家端；0 = 明确失败（主机不能猜目标）。</item>
    /// <item>客户端侧：<paramref name="clientId"/> = 0 表示发给主机。</item>
    /// </list>
    /// 返回 false = 没有注册这个类型 / 通道不可用 / 队列已满（**显式失败**，调用方必须处理）。
    /// </summary>
    public interface IMessageSender
    {
        bool Send<T>(int clientId, T message) where T : class, IMessage<T>;
    }

    /// <summary>
    /// 组合根注册的端口：第三方模块通过 <c>IModuleContext.TryGetPort&lt;IMessageRegistryPort&gt;</c>
    /// 拿到注册表与发送器（没有静态单例；端口随联机模块存活）。
    /// </summary>
    public interface IMessageRegistryPort
    {
        MessageRegistry Registry { get; }
        IMessageSender Sender { get; }
    }

    /// <summary>
    /// 类型 id ↔ protobuf 解析器/处理器 的唯一注册表。
    ///
    /// 区间（与 <c>Protocol/protocol.proto</c> 的 MessageType 注释一致）：
    ///   1..999    SuMultiplayer 内置消息；
    ///   1000..1999 第三方消息（每个模块自己选一个未占用的 id）。
    ///
    /// 分发是**强类型**的：注册时绑定 <see cref="MessageParser{T}"/> 与
    /// <see cref="IMessageHandler{T}"/>，运行时只做一次字典查找 + 一次 protobuf 解析，
    /// 没有反射、没有装箱、没有 per-frame 的委托分配。一个类型可以挂多个处理器（按注册顺序）。
    /// </summary>
    public sealed class MessageRegistry
    {
        public const int BuiltInMin = 1;
        public const int BuiltInMax = 999;
        public const int ThirdPartyMin = 1000;
        public const int ThirdPartyMax = 1999;

        private interface IMessageDispatcher
        {
            void Dispatch(in MessageContext context, byte[] payload);
        }

        private sealed class Registration
        {
            public int TypeId;
            public Type MessageType;
            public IMessageDispatcher Dispatcher;
        }

        /// <summary>不经过 protobuf 解析的原始处理器（只给冻结格式用，例如 live-map 请求）。</summary>
        private sealed class RawRegistration : IMessageDispatcher, IRemovable
        {
            private readonly List<RawEntry> m_handlers = new List<RawEntry>(2);

            private readonly struct RawEntry
            {
                public RawEntry(string owner, Action<MessageContext, byte[]> handler)
                {
                    Owner = owner;
                    Handler = handler;
                }

                public string Owner { get; }
                public Action<MessageContext, byte[]> Handler { get; }
            }

            public void Add(string owner, Action<MessageContext, byte[]> handler) =>
                m_handlers.Add(new RawEntry(owner, handler));

            public void RemoveOwner(string owner)
            {
                for (int i = m_handlers.Count - 1; i >= 0; i--)
                    if (string.Equals(m_handlers[i].Owner, owner, StringComparison.Ordinal))
                        m_handlers.RemoveAt(i);
            }

            void IRemovable.RemoveOwner(string owner) => RemoveOwner(owner);

            public void Dispatch(in MessageContext context, byte[] payload)
            {
                for (int i = 0; i < m_handlers.Count; i++)
                    m_handlers[i].Handler(context, payload);
            }
        }

        /// <summary>一个具体类型 id 的解析器 + 处理器列表。</summary>
        private sealed class Registration<T> : IMessageDispatcher, IRemovable where T : class, IMessage<T>
        {
            private readonly MessageRegistry m_owner;
            private readonly MessageParser<T> m_parser;
            private readonly List<HandlerEntry> m_handlers = new List<HandlerEntry>(2);

            private readonly struct HandlerEntry
            {
                public HandlerEntry(string owner, IMessageHandler<T> handler)
                {
                    Owner = owner;
                    Handler = handler;
                }

                public string Owner { get; }
                public IMessageHandler<T> Handler { get; }
            }

            public Registration(MessageRegistry owner, MessageParser<T> parser)
            {
                m_owner = owner;
                m_parser = parser ?? throw new ArgumentNullException(nameof(parser));
            }

            public void Add(string owner, IMessageHandler<T> handler)
            {
                for (int i = 0; i < m_handlers.Count; i++)
                {
                    if (!string.Equals(m_handlers[i].Owner, owner, StringComparison.Ordinal)) continue;
                    throw new InvalidOperationException(
                        "消息处理器重复注册：type=" + typeof(T).FullName + " owner=" + owner);
                }
                m_handlers.Add(new HandlerEntry(owner, handler));
            }

            public void RemoveOwner(string owner)
            {
                for (int i = m_handlers.Count - 1; i >= 0; i--)
                    if (string.Equals(m_handlers[i].Owner, owner, StringComparison.Ordinal))
                        m_handlers.RemoveAt(i);
            }

            void IRemovable.RemoveOwner(string owner) => RemoveOwner(owner);

            public void Dispatch(in MessageContext context, byte[] payload)
            {
                T message;
                try
                {
                    message = m_parser.ParseFrom(payload);
                }
                catch (InvalidProtocolBufferException e)
                {
                    m_owner.ReportMalformed(context, e);
                    return;
                }
                catch (Exception e)
                {
                    m_owner.ReportMalformed(context, e);
                    return;
                }
                for (int i = 0; i < m_handlers.Count; i++)
                {
                    try
                    {
                        m_handlers[i].Handler.Handle(in context, message);
                    }
                    catch (Exception e)
                    {
                        m_owner.ReportHandlerFault(context, m_handlers[i].Owner, e);
                    }
                }
            }
        }

        private readonly Dictionary<int, Registration> m_byId = new Dictionary<int, Registration>();
        private readonly Dictionary<Type, int> m_idByType = new Dictionary<Type, int>();
        private readonly object m_gate = new object();

        /// <summary>已分发（至少一个处理器执行）的帧数。</summary>
        public long HandledMessages { get; private set; }

        /// <summary>帧类型没有注册（对端用了本端不认识的模块 / 版本不一致）。</summary>
        public long UnhandledMessages { get; private set; }

        /// <summary>载荷不是合法的 protobuf（整帧丢弃，绝不部分应用）。</summary>
        public long MalformedMessages { get; private set; }

        /// <summary>处理器抛异常的次数（帧的其余处理器仍会执行）。</summary>
        public long HandlerFaults { get; private set; }

        /// <summary>显式问题出口（消息被拒绝 / 未知类型 / 处理器异常）。事件本身不记录，由宿主限流写日志。</summary>
        public event Action<string> Problem;

        public static bool IsBuiltInId(int typeId) => typeId >= BuiltInMin && typeId <= BuiltInMax;

        public static bool IsThirdPartyId(int typeId) => typeId >= ThirdPartyMin && typeId <= ThirdPartyMax;

        public static bool IsValidId(int typeId) => IsBuiltInId(typeId) || IsThirdPartyId(typeId);

        /// <summary>
        /// 注册一个消息类型。
        /// </summary>
        /// <param name="typeId">1..1999（内置 1..999 / 第三方 1000..1999）。</param>
        /// <param name="parser">生成的 <c>MyMessage.Parser</c>。</param>
        /// <param name="handler">处理器；发送方只发不收时传 null。</param>
        /// <param name="owner">归属（模块 id）；卸载时按它移除。</param>
        public void Register<T>(int typeId, MessageParser<T> parser, IMessageHandler<T> handler,
            string owner) where T : class, IMessage<T>
        {
            if (!IsValidId(typeId))
                throw new ArgumentOutOfRangeException(nameof(typeId),
                    "消息 id " + typeId + " 不在 1.." + BuiltInMax + "（内置）或 " +
                    ThirdPartyMin + ".." + ThirdPartyMax + "（第三方）内");
            if (string.IsNullOrWhiteSpace(owner))
                throw new ArgumentException("注册消息必须给 owner（模块 id）", nameof(owner));
            if (parser == null) throw new ArgumentNullException(nameof(parser));

            lock (m_gate)
            {
                // 同一个 protobuf 消息类型允许挂在多个 id 上：位置通道就是实例——
                // id=17 承载"客户端→主机的单条快照"、id=18 承载"主机→客户端的批量快照"，
                // 两者线上都是 PlayerPositions（只是 entries 条数不同）。发送端按 id 显式发送
                // （TrySend(...)），所以这里只把首个 id 记为 Send<T> 的默认 id，不覆盖。
                if (!m_byId.TryGetValue(typeId, out Registration registration))
                {
                    var typed = new Registration<T>(this, parser);
                    registration = new Registration
                    {
                        TypeId = typeId,
                        MessageType = typeof(T),
                        Dispatcher = typed
                    };
                    m_byId.Add(typeId, registration);
                    if (!m_idByType.ContainsKey(typeof(T))) m_idByType[typeof(T)] = typeId;
                }
                else
                {
                    if (registration.MessageType != typeof(T))
                        throw new InvalidOperationException(
                            "消息 id=" + typeId + " 已被 " + registration.MessageType.FullName +
                            " 占用，不能再注册 " + typeof(T).FullName +
                            "（第三方请使用 " + ThirdPartyMin + ".." + ThirdPartyMax + " 且不要与别人冲突）");
                }

                if (handler != null)
                {
                    if (registration.Dispatcher is not Registration<T> typed)
                        throw new InvalidOperationException("消息注册表内部不一致：id=" + typeId);
                    typed.Add(owner, handler);
                }
            }
        }

        /// <summary>
        /// 注册"原始字节"处理器（不解析 protobuf）。只用于冻结格式：live-map 的 MapRequest/MapState。
        /// 新消息一律用强类型 <see cref="Register{T}"/>。
        /// </summary>
        public void RegisterRaw(MessageType type, Action<MessageContext, byte[]> handler, string owner)
        {
            int typeId = (int)type;
            if (!IsValidId(typeId))
                throw new ArgumentOutOfRangeException(nameof(type), "消息 id " + typeId + " 非法");
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (string.IsNullOrWhiteSpace(owner))
                throw new ArgumentException("注册消息必须给 owner（模块 id）", nameof(owner));
            lock (m_gate)
            {
                if (m_byId.TryGetValue(typeId, out Registration existing))
                {
                    if (existing.Dispatcher is not RawRegistration)
                        throw new InvalidOperationException("消息 id=" + typeId +
                            " 已经以强类型注册，不能再注册原始处理器");
                    ((RawRegistration)existing.Dispatcher).Add(owner, handler);
                    return;
                }
                var raw = new RawRegistration();
                raw.Add(owner, handler);
                m_byId.Add(typeId, new Registration
                {
                    TypeId = typeId,
                    MessageType = null,
                    Dispatcher = raw
                });
            }
        }

        /// <summary>注册一个模块的全部消息（<see cref="ISuMPMessageModule"/> 的调用入口）。</summary>
        public void RegisterModule(ISuMPMessageModule module)
        {
            if (module == null) throw new ArgumentNullException(nameof(module));
            module.Register(this);
        }

        /// <summary>按归属移除处理器（通道关闭 / 模块下线）。注册项/解析器保留，发送仍然可用。</summary>
        public void UnregisterOwner(string owner)
        {
            if (string.IsNullOrEmpty(owner)) return;
            lock (m_gate)
            {
                foreach (Registration registration in m_byId.Values)
                {
                    // 通过接口调用（不是反射）：AOT 安全。
                    if (registration.Dispatcher is IRemovable removable) removable.RemoveOwner(owner);
                }
            }
        }

        /// <summary>只用于按归属移除处理器（避免对泛型参数做反射）。</summary>
        private interface IRemovable
        {
            void RemoveOwner(string owner);
        }

        public bool TryGetTypeId(Type messageType, out int typeId)
        {
            if (messageType == null) { typeId = 0; return false; }
            lock (m_gate) return m_idByType.TryGetValue(messageType, out typeId);
        }

        /// <summary>分发一帧。返回 false = 没有注册这个类型（由宿主写一行原因，绝不静默）。</summary>
        public bool Dispatch(in ReceivedFrame frame)
        {
            Registration registration;
            lock (m_gate)
            {
                if (!m_byId.TryGetValue((int)frame.Type, out registration))
                {
                    UnhandledMessages++;
                    Problem?.Invoke("收到未注册的消息类型 " + (int)frame.Type + "（" + frame.Type +
                        "，" + frame.Payload.Length + " 字节，clientId=" + frame.ClientId +
                        "）→ 整帧丢弃（对端模块未安装 / id 不一致）");
                    return false;
                }
            }
            var context = new MessageContext(frame.ClientId, frame.Sequence, frame.Type,
                frame.Payload.Length, frame.Payload);
            registration.Dispatcher.Dispatch(in context, frame.Payload);
            HandledMessages++;
            return true;
        }

        internal void ReportProblem(string message) => Problem?.Invoke(message);

        private void ReportMalformed(in MessageContext context, Exception error)
        {
            MalformedMessages++;
            Problem?.Invoke("消息载荷解析失败：type=" + (int)context.Type + "（" + context.Type +
                "，clientId=" + context.ClientId + "，" + context.PayloadBytes + " 字节）→ 整帧丢弃：" +
                error.GetType().Name + " " + error.Message);
        }

        private void ReportHandlerFault(in MessageContext context, string owner, Exception error)
        {
            HandlerFaults++;
            Problem?.Invoke("消息处理器异常：type=" + (int)context.Type + "（" + context.Type +
                "，owner=" + owner + "，clientId=" + context.ClientId + "）→ 该处理器本次被跳过：" +
                error.GetType().Name + " " + error.Message);
        }
    }

    /// <summary>
    /// 通道门面：把 <see cref="MessageRegistry"/> 与"怎么把一帧发出去"绑在一起。
    ///
    /// 生产接入：由 TerrainSyncModule 创建（发送委托 = 它自己的 SendThroughChannel/To），
    /// 主线程泵每帧调用 <see cref="Dispatch"/>；第三方的发送走 <see cref="Sender"/>。
    /// 测试接入：直接 new 一个（发送委托 = TcpTerrainServer/Client.TrySend）。
    /// </summary>
    public sealed class MessageChannel : IMessageSender, IMessageRegistryPort
    {
        private readonly Func<int, MessageType, byte[], bool> m_send;
        private readonly string m_name;

        public MessageChannel(MessageRegistry registry,
            Func<int, MessageType, byte[], bool> send, string name)
        {
            Registry = registry ?? throw new ArgumentNullException(nameof(registry));
            m_send = send ?? throw new ArgumentNullException(nameof(send));
            m_name = string.IsNullOrEmpty(name) ? "message-channel" : name;
        }

        public MessageRegistry Registry { get; }

        public IMessageSender Sender => this;

        public string Name => m_name;

        /// <summary>发出的消息数 / 发送失败数（不可用 / 未注册类型 / 背压）。</summary>
        public long MessagesSent { get; private set; }
        public long SendFailures { get; private set; }
        public long SendUnknownType { get; private set; }

        /// <summary>显式问题出口（转发自 <see cref="MessageRegistry.Problem"/>）。</summary>
        public event Action<string> Problem
        {
            add { Registry.Problem += value; }
            remove { Registry.Problem -= value; }
        }

        /// <summary>主线程泵每帧调用一次。</summary>
        public bool Dispatch(in ReceivedFrame frame) => Registry.Dispatch(in frame);

        public bool Send<T>(int clientId, T message) where T : class, IMessage<T>
        {
            if (message == null) return false;
            if (!Registry.TryGetTypeId(typeof(T), out int typeId))
            {
                SendUnknownType++;
                Registry.ReportProblem("发送失败：消息类型 " + typeof(T).FullName +
                    " 没有注册到 MessageRegistry（先 Register，再 Send）");
                return false;
            }
            if (!m_send(clientId, (MessageType)typeId, message.ToByteArray()))
            {
                SendFailures++;
                return false;
            }
            MessagesSent++;
            return true;
        }

        public string Describe()
        {
            return "name=" + m_name + " sent=" + MessagesSent + " sendFailures=" + SendFailures +
                " sendUnknownType=" + SendUnknownType + " handled=" + Registry.HandledMessages +
                " unhandled=" + Registry.UnhandledMessages + " malformed=" + Registry.MalformedMessages +
                " handlerFaults=" + Registry.HandlerFaults;
        }
    }
}
