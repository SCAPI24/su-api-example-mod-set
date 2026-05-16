using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;

namespace Comms.Drt;

internal class MessageSerializer
{
    private static Dictionary<int, Type> MessageTypesById;

    private static Dictionary<Type, int> MessageIdsByType;

    public int GameTypeID { get; }

    static MessageSerializer()
    {
        MessageTypesById = new Dictionary<int, Type>();
        MessageIdsByType = new Dictionary<Type, int>();
        TypeInfo[] array = (from t in typeof(Message).Assembly.DefinedTypes
                            where typeof(Message).IsAssignableFrom(t) && !t.IsAbstract
                            orderby t.Name
                            select t).ToArray();
        for (int num = 0; num < array.Length; num++)
        {
            MessageTypesById[num] = array[num];
            MessageIdsByType[array[num]] = num;
        }
    }

    public MessageSerializer(int gameTypeID)
    {
        GameTypeID = gameTypeID;
    }

    public Message Read(byte[] bytes, IPEndPoint senderAddress)
    {
        try
        {
            Reader reader = new(bytes);
            int num = reader.ReadInt32();
            if (num != GameTypeID)
            {
                throw new ProtocolViolationException($"Message has invalid game type ID 0x{num:X}, expected 0x{GameTypeID:X}.");
            }
            Message obj = (Message)Activator.CreateInstance(MessageTypesById[reader.ReadPackedInt32()]);
            obj.Read(reader);
            return obj;
        }
        catch (Exception ex)
        {
            throw new MalformedMessageException(ex.Message, senderAddress);
        }
    }

    public byte[] Write(Message message)
    {
        Writer writer = new();
        writer.WriteInt32(GameTypeID);
        writer.WritePackedInt32(MessageIdsByType[message.GetType()]);
        message.Write(writer);
        return writer.GetBytes();
    }
}
