using Engine;
using Game;
using GameEntitySystem;
using System;

namespace PlayerAiMod
{
    /// <summary>
    /// 默认传感器：读取绑定玩家与世界里其它玩家的**公开**状态。
    ///
    /// 只读，不写任何游戏状态。需要读私有后端字段的项目（例如俯仰角在
    /// ComponentLocomotion.m_lookAngles 里）留到移植输入层时一起补，见 README §7。
    ///
    /// Source: Survivalcraft/Game/ComponentLocomotion.cs:303（yaw 提取公式）
    /// Source: Survivalcraft/Game/ComponentInput.cs:91-94（输入是否会被采纳）
    /// Source: Survivalcraft/Game/PlayerData.cs:84 Name / :124 IsReadyForPlaying
    /// Source: Survivalcraft/Game/SubsystemPlayers.cs（ComponentPlayers，含联机远端玩家）
    /// </summary>
    public sealed class PlayerSensor : IAiSensor
    {
        private readonly AiActor m_actor;

        public PlayerSensor(AiActor actor)
        {
            if (actor == null)
                throw new ArgumentNullException(nameof(actor));
            m_actor = actor;
        }

        private ComponentPlayer Player
        {
            get { return m_actor.Player; }
        }

        public bool IsReady
        {
            get
            {
                ComponentPlayer player = Player;
                return player != null
                    && player.ComponentBody != null
                    && player.ComponentHealth != null
                    && GameManager.Project != null;
            }
        }

        public bool IsInputAccepted
        {
            get
            {
                ComponentPlayer player = Player;
                if (player == null || player.PlayerData == null)
                    return false;

                // Source: ComponentInput.cs:91-94
                // !Window.IsActive || !PlayerData.IsReadyForPlaying 时，游戏把 PlayerInput 整个置空，
                // 于是"注入到引擎输入数组里的按键/鼠标"不会被采纳（肉眼看不出，AI 必须自查）。
                return Window.IsActive && player.PlayerData.IsReadyForPlaying;
            }
        }

        public string PlayerName
        {
            get
            {
                try
                {
                    return Player != null && Player.PlayerData != null ? Player.PlayerData.Name : null;
                }
                catch (Exception)
                {
                    return null;
                }
            }
        }

        public Vector3 Position
        {
            get
            {
                ComponentPlayer player = Player;
                return player != null && player.ComponentBody != null
                    ? player.ComponentBody.Position
                    : Vector3.Zero;
            }
        }

        public Vector3 EyePosition
        {
            get
            {
                ComponentPlayer player = Player;
                if (player == null)
                    return Vector3.Zero;
                return player.ComponentCreatureModel != null
                    ? player.ComponentCreatureModel.EyePosition
                    : Position;
            }
        }

        public Vector3 Velocity
        {
            get
            {
                ComponentPlayer player = Player;
                return player != null && player.ComponentBody != null
                    ? player.ComponentBody.Velocity
                    : Vector3.Zero;
            }
        }

        public float YawRadians
        {
            get
            {
                ComponentPlayer player = Player;
                if (player == null || player.ComponentBody == null)
                    return 0f;

                Quaternion rotation = player.ComponentBody.Rotation;
                // Source: ComponentLocomotion.cs:303 —— 游戏自身就是这么从四元数里取 yaw 的。
                return MathUtils.Atan2(
                    2f * rotation.Y * rotation.W - 2f * rotation.X * rotation.Z,
                    1f - 2f * rotation.Y * rotation.Y - 2f * rotation.Z * rotation.Z);
            }
        }

        public bool TryGetPitch(out float pitchRadians)
        {
            // TODO(输入层移植): ComponentLocomotion 的俯仰角在私有后端字段 m_lookAngles.Y
            //（setter 为 private，范围 ±82°）。等执行器接入输入层时，一并用同一套
            // ModParentField 通道读取，避免现在就把白名单机制拆成两处。
            pitchRadians = 0f;
            return false;
        }

        public float Health
        {
            get
            {
                ComponentPlayer player = Player;
                return player != null && player.ComponentHealth != null
                    ? player.ComponentHealth.Health
                    : 0f;
            }
        }

        public bool TryFindPlayer(string name, out AiActorView view)
        {
            view = default(AiActorView);
            if (string.IsNullOrEmpty(name))
                return false;

            SubsystemPlayers players = FindPlayers();
            if (players == null)
                return false;

            for (int i = 0; i < players.ComponentPlayers.Count; i++)
            {
                ComponentPlayer candidate = players.ComponentPlayers[i];
                string candidateName = null;
                try
                {
                    candidateName = candidate != null && candidate.PlayerData != null
                        ? candidate.PlayerData.Name
                        : null;
                }
                catch (Exception)
                {
                    candidateName = null;
                }

                if (candidateName != null
                    && string.Equals(candidateName, name, StringComparison.OrdinalIgnoreCase))
                {
                    view = Describe(candidate);
                    return true;
                }
            }

            return false;
        }

        public bool TryFindNearestPlayer(out AiActorView view)
        {
            view = default(AiActorView);

            SubsystemPlayers players = FindPlayers();
            if (players == null)
                return false;

            bool found = false;
            float bestDistance = float.MaxValue;

            for (int i = 0; i < players.ComponentPlayers.Count; i++)
            {
                ComponentPlayer candidate = players.ComponentPlayers[i];
                if (candidate == null || ReferenceEquals(candidate, Player))
                    continue;

                AiActorView candidateView = Describe(candidate);
                if (candidateView.Distance < bestDistance)
                {
                    bestDistance = candidateView.Distance;
                    view = candidateView;
                    found = true;
                }
            }

            return found;
        }

        private SubsystemPlayers FindPlayers()
        {
            Project project = GameManager.Project;
            return project != null ? project.FindSubsystem<SubsystemPlayers>(false) : null;
        }

        private AiActorView Describe(ComponentPlayer candidate)
        {
            var view = new AiActorView
            {
                IsSelf = ReferenceEquals(candidate, Player),
                IsPlayer = true,
                PlayerIndex = -1,
                Name = null,
                Position = Vector3.Zero,
                Velocity = Vector3.Zero,
                Distance = 0f
            };

            if (candidate == null)
                return view;

            try
            {
                if (candidate.PlayerData != null)
                {
                    view.PlayerIndex = candidate.PlayerData.PlayerIndex;
                    view.Name = candidate.PlayerData.Name;
                }
            }
            catch (Exception)
            {
                // 名字/索引读取失败不影响位置读取。
            }

            if (candidate.ComponentBody != null)
            {
                view.Position = candidate.ComponentBody.Position;
                view.Velocity = candidate.ComponentBody.Velocity;
                view.Distance = Vector3.Distance(view.Position, Position);
            }

            return view;
        }
    }
}
