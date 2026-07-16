using System;
using System.Net;
using Comms;

namespace ScMultiplayer
{
    [Serializable]
    public class ChatMessage : Message
    {
        public string Sender { get; set; }
        public string SenderIdentity { get; set; }
        public string Text { get; set; }
        public DateTime Timestamp { get; set; }
        public Guid MessageId { get; set; }

        public ChatMessage()
        {
            MessageId = Guid.NewGuid();
            Timestamp = DateTime.UtcNow;
        }

        public ChatMessage(string sender, string senderIdentity, string text) : this()
        {
            Sender = sender;
            SenderIdentity = senderIdentity;
            Text = text;
        }

        protected override void Read(SuReader reader)
        {
            // 读取消息元数据
            byte[] guidBytes = reader.ReadFixedBytes(16);
            MessageId = new Guid(guidBytes);
            Timestamp = DateTime.FromBinary(reader.ReadInt64());

            // 读取消息内容
            Sender = reader.ReadString();
            SenderIdentity = reader.ReadString();
            Text = reader.ReadString();
        }

        protected override void Write(SuWriter writer)
        {
            // 写入消息元数据
            writer.WriteFixedBytes(MessageId.ToByteArray());
            writer.WriteInt64(Timestamp.ToBinary());

            // 写入消息内容
            writer.WriteString(Sender ?? string.Empty);
            writer.WriteString(SenderIdentity ?? string.Empty);
            writer.WriteString(Text ?? string.Empty);
        }

        public override string ToString()
        {
            return $"[{Timestamp:HH:mm:ss}] {Sender}: {Text}";
        }
    }
}
