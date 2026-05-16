using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using Comms;

namespace ScMultiplayer
{
    [Serializable]
    public abstract class Message
    {
        private static Dictionary<int, Type> MessageTypesById;
        private static Dictionary<Type, int> MessageIdsByType;

        /// <summary>
        /// 消息发送者的终端地址
        /// </summary>
        public IPEndPoint SenderEndPoint { get; set; }

        static Message()
        {
            MessageTypesById = new Dictionary<int, Type>();
            MessageIdsByType = new Dictionary<Type, int>();

            TypeInfo[] messageTypes = (from t in typeof(Message).Assembly.DefinedTypes
                                       where typeof(Message).IsAssignableFrom(t) && !t.IsAbstract
                                       orderby t.Name
                                       select t).ToArray();

            for (int i = 0; i < messageTypes.Length; i++)
            {
                MessageTypesById[i] = messageTypes[i];
                MessageIdsByType[messageTypes[i]] = i;
            }
        }

        public static Message Read(byte[] bytes, IPEndPoint senderEndPoint = null)
        {
            SuReader reader = new SuReader(bytes);

            int messageTypeId = reader.ReadPackedInt32();
            if (!MessageTypesById.TryGetValue(messageTypeId, out Type messageType))
            {
                throw new ProtocolViolationException($"Unknown message type ID: {messageTypeId}");
            }

            Message message = (Message)Activator.CreateInstance(messageType);

            // 从数据流中读取发送者信息
            bool hasSender = reader.ReadBoolean();
            if (hasSender)
            {
                message.SenderEndPoint = reader.ReadIPEndPoint();
            }
            else
            {
                // 如果数据流中没有发送者信息，使用传入的发送者信息
                message.SenderEndPoint = senderEndPoint;
            }

            message.Read(reader);
            return message;
        }

        public static byte[] Write(Message message, IPEndPoint senderEndPoint = null)
        {
            SuWriter writer = new SuWriter();

            if (!MessageIdsByType.TryGetValue(message.GetType(), out int messageTypeId))
            {
                throw new InvalidOperationException($"Unregistered message type: {message.GetType()}");
            }

            writer.WritePackedInt32(messageTypeId);

            // 序列化发送者信息
            bool hasSenderToSerialize = message.SenderEndPoint != null || senderEndPoint != null;
            writer.WriteBoolean(hasSenderToSerialize);

            if (hasSenderToSerialize)
            {
                // 优先使用消息中已有的发送者信息，否则使用传入的发送者信息
                IPEndPoint endPointToSerialize = message.SenderEndPoint ?? senderEndPoint;
                writer.WriteIPEndPoint(endPointToSerialize);
            }

            message.Write(writer);
            return writer.GetBytes();
        }

        /// <summary>
        /// 便捷方法：写入消息并记录发送者
        /// </summary>
        public static byte[] WriteWithSender(Message message, IPEndPoint senderEndPoint)
        {
            // 设置发送者信息后序列化
            message.SenderEndPoint = senderEndPoint;
            return Write(message, null); // 传入null，因为发送者信息已经在message中
        }

        /// <summary>
        /// 便捷方法：读取消息并记录发送者
        /// </summary>
        public static Message ReadWithSender(byte[] bytes, IPEndPoint senderEndPoint)
        {
            return Read(bytes, senderEndPoint);
        }

        protected abstract void Read(SuReader reader);
        protected abstract void Write(SuWriter writer);

        /// <summary>
        /// 获取发送者的IP地址（如果存在）
        /// </summary>
        public IPAddress GetSenderAddress()
        {
            return SenderEndPoint?.Address;
        }

        /// <summary>
        /// 获取发送者的端口号（如果存在）
        /// </summary>
        public int? GetSenderPort()
        {
            return SenderEndPoint?.Port;
        }

        /// <summary>
        /// 检查消息是否有发送者信息
        /// </summary>
        public bool HasSender()
        {
            return SenderEndPoint != null;
        }

        /// <summary>
        /// 获取发送者的字符串表示
        /// </summary>
        public string GetSenderString()
        {
            return SenderEndPoint?.ToString() ?? "Unknown";
        }

        /// <summary>
        /// 设置发送者信息（链式调用）
        /// </summary>
        public Message SetSender(IPEndPoint senderEndPoint)
        {
            SenderEndPoint = senderEndPoint;
            return this;
        }

        /// <summary>
        /// 设置发送者信息（链式调用）
        /// </summary>
        public Message SetSender(IPAddress address, int port)
        {
            SenderEndPoint = new IPEndPoint(address, port);
            return this;
        }
    }
}