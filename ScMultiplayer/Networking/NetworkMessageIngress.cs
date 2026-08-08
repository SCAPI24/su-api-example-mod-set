using System;

namespace ScMultiplayer
{
    // Source: Mod/ScMultiplayer/Plug/ScMultiplayer.cs:ScMultiplayer.Client_GameStep
    // Transport ingress owns only framing, decoding and loopback filtering. Business handlers
    // remain in ScMultiplayer until their domain modules are migrated.
    internal static class NetworkMessageIngress
    {
        public static bool TryDecode(byte[] inputBytes, int localPort,
            out Message message, out string error)
        {
            message = null;
            error = null;
            if (inputBytes == null || inputBytes.Length == 0)
                return false;

            try
            {
                message = Message.Read(inputBytes);
            }
            catch (Exception ex)
            {
                error = $"Bytes={inputBytes.Length}, Error={ex.Message}";
                return false;
            }

            // Source: Mod/Comms/Comms.Drt/Func/Client/Client.cs:Client.SendDirectInput
            // A broadcast can be looped back by the local transport. Preserve the existing
            // behavior and let the sender-side state remain authoritative.
            return message.GetSenderPort() != localPort;
        }
    }
}
