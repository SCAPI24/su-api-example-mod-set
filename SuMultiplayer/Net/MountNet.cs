using System;
using Engine;
using Google.Protobuf;
using P = SuMultiplayer.Protocol.Mount;
using PM = SuMultiplayer.Protocol.Position;

namespace SuMultiplayer.Net
{
    /// <summary>MountState 状态（与 proto int32 一一对应；append-only）。</summary>
    public static class MountStateKind
    {
        public const int Mounting = 1;
        public const int Mounted = 2;
        public const int Dismounting = 3;
        public const int Dismounted = 4;
        public const int Rejected = 5;

        public static bool IsKnown(int state) => state >= Mounting && state <= Rejected;

        public static string Name(int state)
        {
            switch (state)
            {
                case Mounting: return "mounting";
                case Mounted: return "mounted";
                case Dismounting: return "dismounting";
                case Dismounted: return "dismounted";
                case Rejected: return "rejected";
                default: return "state-" + state;
            }
        }
    }

    /// <summary>客户端 → 主机：骑乘按钮按下（引擎 ComponentGui.ToggleMount 边沿）。</summary>
    public readonly struct MountRequestData
    {
        public MountRequestData(long sequence, bool mount, int mountKey,
            Vector3 bodyPosition, Quaternion bodyRotation, Vector2 lookAngles)
        {
            Sequence = sequence;
            Mount = mount;
            MountKey = mountKey;
            BodyPosition = bodyPosition;
            BodyRotation = bodyRotation;
            LookAngles = lookAngles;
        }

        public long Sequence { get; }
        public bool Mount { get; }
        /// <summary>客户端瞄准的坐骑网络 key（0 = 没有目标；主机会按位置重选）。</summary>
        public int MountKey { get; }
        /// <summary>按键瞬间的身体位姿（FindNearestMount 位置敏感；主机先校正再选择）。</summary>
        public Vector3 BodyPosition { get; }
        public Quaternion BodyRotation { get; }
        public Vector2 LookAngles { get; }

        public string Describe() => "seq=" + Sequence + " " + (Mount ? "mount" : "dismount") +
            " key=" + MountKey + " pos=" + BodyPosition.X.ToString("F1") + "," +
            BodyPosition.Z.ToString("F1");
    }

    /// <summary>主机 → 各端：某骑手的权威乘骑状态。</summary>
    public readonly struct MountStateData
    {
        public MountStateData(int riderClientId, long sequence, long stateSequence, int state,
            int mountKey, Vector3 mountPosition, Quaternion mountRotation)
        {
            RiderClientId = riderClientId;
            Sequence = sequence;
            StateSequence = stateSequence;
            State = state;
            MountKey = mountKey;
            MountPosition = mountPosition;
            MountRotation = mountRotation;
        }

        public int RiderClientId { get; }
        /// <summary>产生该状态的请求序号（0 = 主机原生下骑：被甩落 / 船翻了等）。</summary>
        public long Sequence { get; }
        /// <summary>主机按骑手单调递增；客户端丢弃过期的。</summary>
        public long StateSequence { get; }
        /// <summary><see cref="MountStateKind"/>。</summary>
        public int State { get; }
        /// <summary>坐骑网络 key（0 = 无）。</summary>
        public int MountKey { get; }
        public Vector3 MountPosition { get; }
        public Quaternion MountRotation { get; }

        public string Describe() => "rider=" + RiderClientId + " seq=" + Sequence +
            " stateSeq=" + StateSequence + " " + MountStateKind.Name(State) + " key=" + MountKey;
    }

    /// <summary>
    /// Mount 协议编解码（纯逻辑，P:\tmp\tsverify 可直接编译自检）。非法载荷整条拒绝，
    /// 不做"猜一个默认值"。
    /// </summary>
    public static class MountNetCodec
    {
        private static bool IsFinite(float v) => !float.IsNaN(v) && !float.IsInfinity(v);

        private static bool Finite(Vector3 v) => IsFinite(v.X) && IsFinite(v.Y) && IsFinite(v.Z);

        // Vec2/Vec3/Quat 定义在 sump.position（position.proto），Mount 协议直接 import 复用。
        private static PM.Vec3 Vec(Vector3 v) => new PM.Vec3 { X = v.X, Y = v.Y, Z = v.Z };

        private static PM.Quat Quat(Quaternion q) =>
            new PM.Quat { X = q.X, Y = q.Y, Z = q.Z, W = q.W };

        public static byte[] EncodeRequest(in MountRequestData request)
        {
            return new P.MountRequest
            {
                Sequence = request.Sequence,
                Mount = request.Mount,
                MountKey = request.MountKey,
                BodyPosition = Vec(request.BodyPosition),
                BodyRotation = Quat(request.BodyRotation),
                LookAngles = new PM.Vec2 { X = request.LookAngles.X, Y = request.LookAngles.Y }
            }.ToByteArray();
        }

        public static bool TryDecodeRequest(byte[] payload, out MountRequestData request,
            out string reason)
        {
            request = default;
            reason = null;
            if (payload == null || payload.Length == 0) { reason = "载荷为空"; return false; }
            P.MountRequest message;
            try { message = P.MountRequest.Parser.ParseFrom(payload); }
            catch (Exception e) { reason = "载荷格式错误：" + e.GetType().Name; return false; }
            if (message.Sequence <= 0) { reason = "sequence-" + message.Sequence; return false; }
            if (message.BodyPosition == null || message.BodyRotation == null ||
                message.LookAngles == null)
            {
                reason = "pose-missing";
                return false;
            }
            var position = new Vector3(message.BodyPosition.X, message.BodyPosition.Y,
                message.BodyPosition.Z);
            var rotation = new Quaternion(message.BodyRotation.X, message.BodyRotation.Y,
                message.BodyRotation.Z, message.BodyRotation.W);
            var look = new Vector2(message.LookAngles.X, message.LookAngles.Y);
            if (!Finite(position) || !float.IsFinite(rotation.X) || !float.IsFinite(rotation.W) ||
                !float.IsFinite(look.X) || !float.IsFinite(look.Y))
            {
                reason = "pose-nonfinite";
                return false;
            }
            request = new MountRequestData(message.Sequence, message.Mount, message.MountKey,
                position, rotation, look);
            return true;
        }

        public static byte[] EncodeState(in MountStateData state)
        {
            return new P.MountState
            {
                RiderClientId = state.RiderClientId,
                Sequence = state.Sequence,
                StateSequence = state.StateSequence,
                State = state.State,
                MountKey = state.MountKey,
                MountPosition = Vec(state.MountPosition),
                MountRotation = Quat(state.MountRotation)
            }.ToByteArray();
        }

        public static bool TryDecodeState(byte[] payload, out MountStateData state,
            out string reason)
        {
            state = default;
            reason = null;
            if (payload == null || payload.Length == 0) { reason = "载荷为空"; return false; }
            P.MountState message;
            try { message = P.MountState.Parser.ParseFrom(payload); }
            catch (Exception e) { reason = "载荷格式错误：" + e.GetType().Name; return false; }
            if (message.RiderClientId < 0) { reason = "rider-" + message.RiderClientId; return false; }
            if (!MountStateKind.IsKnown(message.State))
            {
                reason = "state-" + message.State;
                return false;
            }
            if (message.MountPosition == null || message.MountRotation == null)
            {
                reason = "mount-pose-missing";
                return false;
            }
            var position = new Vector3(message.MountPosition.X, message.MountPosition.Y,
                message.MountPosition.Z);
            var rotation = new Quaternion(message.MountRotation.X, message.MountRotation.Y,
                message.MountRotation.Z, message.MountRotation.W);
            if (!Finite(position))
            {
                reason = "mount-pose-nonfinite";
                return false;
            }
            state = new MountStateData(message.RiderClientId, message.Sequence,
                message.StateSequence, message.State, message.MountKey, position, rotation);
            return true;
        }
    }
}
