using System;
using Engine;
using SuMultiplayer.Net;

namespace SuMultiplayer.Modules
{
    /// <summary>
    /// 乘骑复制的引擎端口（实现 = Engine/MountReplicaEngine.cs，组合根注入）。
    /// 模块（消息层）只认这个接口：不引用引擎类型，P:\tmp\tsverify 可直接编译自检。
    /// </summary>
    public interface IMountReplicaPort
    {
        /// <summary>客户端：骑乘按钮边沿已采集（模块编码后发给主机）。</summary>
        event Action<MountRequestData> RequestSubmitted;

        /// <summary>主机：适配层主动广播的权威状态（如原生甩落）。</summary>
        event Action<MountStateData> StateBroadcast;

        /// <summary>
        /// 主机：执行一条乘骑请求。ok = stateKind/mountKey 有效（Mounting/Dismounting）；
        /// !ok = Rejected（reason 写明）。引擎原生 FindNearestMount / StartMounting /
        /// StartDismounting 都在这后面跑。
        /// </summary>
        bool TryExecuteHostRequest(int clientId, in MountRequestData request,
            out int stateKind, out int mountKey, out string reason);

        /// <summary>主机：解析某骑手当前权威骑乘姿态（回执里带给观察端对位）。</summary>
        void ResolveRiderPose(int clientId, out Vector3 mountPosition, out Quaternion mountRotation);

        /// <summary>两端：应用权威状态（本机骑手按 state_sequence；观察端同样镜像）。</summary>
        bool TryPresentState(in MountStateData state);

        /// <summary>状态行诊断。</summary>
        string Describe();
    }
}
