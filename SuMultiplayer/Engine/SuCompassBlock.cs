using Engine;
using Engine.Graphics;
using Game;
using GameEntitySystem;

namespace SuMultiplayer.Engine
{
    /// <summary>
    /// 指南针绘制替换（Block Index 117，2026-10-27）。
    ///
    /// 原版 <see cref="CompassBlock.DrawBlock"/> 在无磁铁时只从**本端** SubsystemPlayers.PlayersData
    /// 里找最近玩家出生点；联机世界里远端角色不在本端 PlayersData，所以同一个角色在客户端自己、
    /// 主机端、其他客户端看到的指针方向不同。
    ///
    /// 本类保持原版模型/尺寸/绘制，只有目标选择改为：
    ///   ① 有磁铁 → 原版磁铁目标（磁铁区块已由地形同步，三端一致）；
    ///   ② 无磁铁 → 在 Project 的 ComponentPlayers（本机 + 全部远端化身）里找离指南针手部
    ///      世界位置最近的玩家，指该玩家 PlayerData.SpawnPosition（主机权威复活点表已同步到
    ///      每个端）；没有可用数据 → 退回原版行为。
    ///
    /// 这样 C1 自己、主机看 C1、其他客户端看 C1 的指南针会指向同一个点。
    /// Source: Survivalcraft/Game/CompassBlock.cs:CompassBlock.DrawBlock
    /// </summary>
    public sealed class CompassBlock : global::Game.CompassBlock
    {
        private BlockMesh m_caseMesh;
        private BlockMesh m_pointerMesh;

        public override void Initialize()
        {
            base.Initialize();
            Model model = ContentManager.Get<Model>("Models/Compass");
            Matrix caseTransform = BlockMesh.GetBoneAbsoluteTransform(model.FindMesh("Case").ParentBone);
            Matrix pointerTransform = BlockMesh.GetBoneAbsoluteTransform(model.FindMesh("Pointer").ParentBone);
            m_caseMesh = new BlockMesh();
            m_caseMesh.AppendModelMeshPart(model.FindMesh("Case").MeshParts[0],
                caseTransform * Matrix.CreateTranslation(0f, -0.01f, 0f), makeEmissive: false,
                flipWindingOrder: false, doubleSided: true, flipNormals: false, Color.White);
            m_pointerMesh = new BlockMesh();
            m_pointerMesh.AppendModelMeshPart(model.FindMesh("Pointer").MeshParts[0],
                pointerTransform * Matrix.CreateTranslation(0f, -0.01f, 0f), makeEmissive: false,
                flipWindingOrder: false, doubleSided: false, flipNormals: false, Color.White);
        }

        public override void DrawBlock(PrimitivesRenderer3D primitivesRenderer, int value, Color color,
            float size, ref Matrix matrix, DrawBlockEnvironmentData environmentData)
        {
            float radians = 0f;
            if (environmentData != null && environmentData.SubsystemTerrain != null &&
                m_caseMesh != null && m_pointerMesh != null)
            {
                try
                {
                    Vector3 forward = environmentData.InWorldMatrix.Forward;
                    Vector3 translation = environmentData.InWorldMatrix.Translation;
                    Vector3 target = ResolveCompassTarget(environmentData.SubsystemTerrain, translation);
                    Vector3 delta = translation - target;
                    radians = Vector2.Angle(v2: new Vector2(forward.X, forward.Z),
                        v1: new Vector2(delta.X, delta.Z));
                }
                catch (System.Exception)
                {
                    // 目标解析失败不能把方块画崩：退回原版指北（0 弧度，与磁铁/玩家都无数据时一致）。
                    radians = 0f;
                }
            }
            Matrix caseMatrix = matrix;
            Matrix pointerMatrix = Matrix.CreateRotationY(radians) * matrix;
            BlocksManager.DrawMeshBlock(primitivesRenderer, m_caseMesh, color, size * 6f,
                ref caseMatrix, environmentData);
            BlocksManager.DrawMeshBlock(primitivesRenderer, m_pointerMesh, color, size * 6f,
                ref pointerMatrix, environmentData);
        }

        private static Vector3 ResolveCompassTarget(SubsystemTerrain terrain, Vector3 translation)
        {
            Project project = terrain?.Project;
            if (project == null) return translation;
            SubsystemMagnetBlockBehavior magnet =
                project.FindSubsystem<SubsystemMagnetBlockBehavior>(throwOnError: false);
            // 有磁铁：磁铁是全世界共享的区块数据，三端天然一致，保持原版语义。
            if (magnet != null && magnet.MagnetsCount > 0)
                return magnet.FindNearestCompassTarget(translation);
            SubsystemPlayers players = project.FindSubsystem<SubsystemPlayers>(throwOnError: false);
            if (players != null)
            {
                // ComponentPlayers 同时包含本机玩家 + 本端创建的远端化身（主机/客户端都一样）。
                // 指南针在手里时离持有者最近，选“离手最近的玩家”即该指南针的归属角色。
                ComponentPlayer nearest = null;
                float nearestDistance = float.MaxValue;
                var componentPlayers = players.ComponentPlayers;
                for (int i = 0; i < componentPlayers.Count; i++)
                {
                    ComponentPlayer player = componentPlayers[i];
                    ComponentBody body = player?.ComponentBody;
                    if (body == null) continue;
                    float distance = Vector3.DistanceSquared(body.Position, translation);
                    if (distance >= nearestDistance) continue;
                    nearestDistance = distance;
                    nearest = player;
                }
                Vector3 spawn = nearest?.PlayerData?.SpawnPosition ?? Vector3.Zero;
                if (spawn != Vector3.Zero && IsFinite(spawn))
                    return spawn + new Vector3(0.5f);
            }
            // 没有磁铁也没有可用玩家数据：保持原版退化路径（不猜一个坐标）。
            return magnet != null ? magnet.FindNearestCompassTarget(translation) : translation;
        }

        private static bool IsFinite(Vector3 value) =>
            !float.IsNaN(value.X) && !float.IsInfinity(value.X) &&
            !float.IsNaN(value.Y) && !float.IsInfinity(value.Y) &&
            !float.IsNaN(value.Z) && !float.IsInfinity(value.Z);
    }
}
