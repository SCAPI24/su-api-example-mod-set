using Game;
using System;

namespace ScMultiplayer
{
    /// <summary>
    /// 联机连接状态机，基于 Game.StateMachine
    /// 管理从扫描到游玩的全生命周期
    /// Source: Survivalcraft/Game/StateMachine.cs
    /// </summary>
    public class NetworkConnectionStateMachine
    {
        public enum ConnectionState
        {
            Disconnected,
            Discovering,
            WaitingForWorld,
            WorldDownloading,
            Playing
        }

        private readonly StateMachine m_fsm = new StateMachine();
        private Action<string> m_log; // 避免静态引用 Engine.Log

        public ConnectionState CurrentState { get; private set; } = ConnectionState.Disconnected;
        public Action OnDiscoveringEnter;
        public Action OnDiscoveringUpdate;
        public Action OnWaitingForWorldEnter;
        public Action<float> OnWaitingForWorldUpdate; // dt (带超时)
        public Action OnWorldDownloadingEnter;
        public Action OnPlayingEnter;
        public Action OnPlayingUpdate;
        public Action OnDisconnectedEnter;

        private float m_worldWaitTime = 0f;
        private const float WorldWaitTimeout = 30f;

        public NetworkConnectionStateMachine(Action<string> logger = null)
        {
            m_log = logger ?? (_ => { });
            BuildStates();
        }

        private void BuildStates()
        {
            // Disconnected
            m_fsm.AddState(nameof(ConnectionState.Disconnected),
                enter: () => { CurrentState = ConnectionState.Disconnected; m_log("[FSM] Disconnected"); OnDisconnectedEnter?.Invoke(); },
                update: null,
                leave: null);

            // Discovering
            m_fsm.AddState(nameof(ConnectionState.Discovering),
                enter: () => { CurrentState = ConnectionState.Discovering; m_log("[FSM] Discovering LAN servers..."); OnDiscoveringEnter?.Invoke(); },
                update: () => { OnDiscoveringUpdate?.Invoke(); },
                leave: null);

            // WaitingForWorld
            m_fsm.AddState(nameof(ConnectionState.WaitingForWorld),
                enter: () => { CurrentState = ConnectionState.WaitingForWorld; m_worldWaitTime = 0f; m_log("[FSM] Waiting for world data..."); OnWaitingForWorldEnter?.Invoke(); },
                update: () =>
                {
                    OnWaitingForWorldUpdate?.Invoke(m_worldWaitTime);
                    // 注意: 超时检测由外部驱动 (传入 dt)
                },
                leave: null);

            // WorldDownloading
            m_fsm.AddState(nameof(ConnectionState.WorldDownloading),
                enter: () => { CurrentState = ConnectionState.WorldDownloading; m_log("[FSM] Downloading world..."); OnWorldDownloadingEnter?.Invoke(); },
                update: null,
                leave: null);

            // Playing
            m_fsm.AddState(nameof(ConnectionState.Playing),
                enter: () => { CurrentState = ConnectionState.Playing; m_log("[FSM] Playing"); OnPlayingEnter?.Invoke(); },
                update: () => { OnPlayingUpdate?.Invoke(); },
                leave: null);
        }

        public void TickWaitingForWorld(float dt)
        {
            if (CurrentState != ConnectionState.WaitingForWorld) return;
            m_worldWaitTime += dt;
            OnWaitingForWorldUpdate?.Invoke(m_worldWaitTime);
            if (m_worldWaitTime > WorldWaitTimeout)
            {
                m_log("[FSM] World download timeout, going back to Disconnected");
                TransitionTo(ConnectionState.Disconnected);
            }
        }

        public void TransitionTo(ConnectionState state)
        {
            // Source: Survivalcraft/Game/StateMachine.cs:StateMachine.TransitionTo
            // Enum member names can be renamed by Obfuscar, so never use ToString() as a state key.
            m_fsm.TransitionTo(GetStateName(state));
        }

        private static string GetStateName(ConnectionState state)
        {
            switch (state)
            {
                case ConnectionState.Disconnected: return "Disconnected";
                case ConnectionState.Discovering: return "Discovering";
                case ConnectionState.WaitingForWorld: return "WaitingForWorld";
                case ConnectionState.WorldDownloading: return "WorldDownloading";
                case ConnectionState.Playing: return "Playing";
                default: throw new ArgumentOutOfRangeException(nameof(state), state, null);
            }
        }

        public void Update()
        {
            m_fsm.Update();
        }
    }

    /// <summary>
    /// 世界下载状态机，管理导入流程
    /// </summary>
    public class WorldDownloadStateMachine
    {
        public enum DownloadState
        {
            Idle,
            Requesting,     // 等待 Host 发送世界
            Receiving,      // 收到 PakWorld, 正在导入
            Importing,      // WorldsManager.ImportWorld
            Complete,       // 导入完成, 进入游戏
            Failed
        }

        private readonly StateMachine m_fsm = new StateMachine();
        private Action<string> m_log;

        public DownloadState CurrentState { get; private set; } = DownloadState.Idle;
        public Action OnRequestingEnter;
        public Action OnReceivingEnter;
        public Action OnImportingEnter;
        public Action OnCompleteEnter;
        public Action<string> OnFailedEnter;

        public WorldDownloadStateMachine(Action<string> logger = null)
        {
            m_log = logger ?? (_ => { });
            BuildStates();
        }

        private void BuildStates()
        {
            m_fsm.AddState(nameof(DownloadState.Idle), null, null, null);
            m_fsm.AddState(nameof(DownloadState.Requesting),
                enter: () => { CurrentState = DownloadState.Requesting; m_log("[DL] Requesting world..."); OnRequestingEnter?.Invoke(); },
                update: null, leave: null);
            m_fsm.AddState(nameof(DownloadState.Receiving),
                enter: () => { CurrentState = DownloadState.Receiving; m_log("[DL] Receiving world data..."); OnReceivingEnter?.Invoke(); },
                update: null, leave: null);
            m_fsm.AddState(nameof(DownloadState.Importing),
                enter: () => { CurrentState = DownloadState.Importing; m_log("[DL] Importing world..."); OnImportingEnter?.Invoke(); },
                update: null, leave: null);
            m_fsm.AddState(nameof(DownloadState.Complete),
                enter: () => { CurrentState = DownloadState.Complete; m_log("[DL] World download complete!"); OnCompleteEnter?.Invoke(); },
                update: null, leave: null);
            m_fsm.AddState(nameof(DownloadState.Failed),
                enter: () => { CurrentState = DownloadState.Failed; m_log("[DL] World download failed"); },
                update: null, leave: null);
        }

        public void TransitionTo(DownloadState state, string failReason = null)
        {
            if (state == DownloadState.Failed)
            {
                CurrentState = DownloadState.Failed;
                OnFailedEnter?.Invoke(failReason ?? "Unknown error");
                return;
            }
            // Source: Survivalcraft/Game/StateMachine.cs:StateMachine.TransitionTo
            // Keep protocol state keys stable when enum fields are obfuscated.
            m_fsm.TransitionTo(GetStateName(state));
        }

        private static string GetStateName(DownloadState state)
        {
            switch (state)
            {
                case DownloadState.Idle: return "Idle";
                case DownloadState.Requesting: return "Requesting";
                case DownloadState.Receiving: return "Receiving";
                case DownloadState.Importing: return "Importing";
                case DownloadState.Complete: return "Complete";
                case DownloadState.Failed: return "Failed";
                default: throw new ArgumentOutOfRangeException(nameof(state), state, null);
            }
        }

        public void Update()
        {
            m_fsm.Update();
        }
    }
}
