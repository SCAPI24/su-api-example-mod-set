using Engine;
using Engine.Graphics;
using Game;
using GameEntitySystem;
using System;
using System.Collections.Generic;
using TemplatesDatabase;

namespace SuApocalypse
{
    // Fixed top-down camera — ignores all input, locks angle/distance
    // UsesMovementControls=false → mouse not captured, cursor visible, MousePosition available
    // IsEntityControlEnabled=true → Dig/Hit/Interact preserved
    // We handle movement/facing ourselves via CameraMove/CameraLook
    public class SuTopDownCamera : BasePerspectiveCamera
    {
        private const float CameraAngleY = 1.05f;   // ~60 degrees from horizontal
        private const float CameraDistance = 14f;

        private Vector3 m_position;

        public override bool UsesMovementControls => false;  // Mouse not captured, cursor visible
        public override bool IsEntityControlEnabled => true;  // Keep Dig/Hit/Interact available

        public SuTopDownCamera(GameWidget gameWidget) : base(gameWidget) { }

        public override void Activate(Camera previousCamera)
        {
            // Start from previous camera position for smooth transition
            m_position = previousCamera.ViewPosition;
        }

        public override void Update(float dt)
        {
            ComponentCreature target = GameWidget.Target;
            if (target == null) return;

            ComponentBody body = target.ComponentBody;
            Vector3 targetPos = body.Position + 0.9f * body.BoxSize.Y * Vector3.UnitY;

            // Fixed offset: camera above and behind the target at fixed angle
            Vector3 offset = Vector3.Transform(
                new Vector3(CameraDistance, 0f, 0f),
                Matrix.CreateFromYawPitchRoll(0f, 0f, CameraAngleY));

            Vector3 desiredPos = targetPos + offset;

            // Smooth camera movement
            if (Vector3.Distance(desiredPos, m_position) < 10f)
            {
                float t = MathUtils.Saturate(10f * dt);
                m_position += t * (desiredPos - m_position);
            }
            else
            {
                m_position = desiredPos;
            }

            // Look at target from camera position
            Vector3 direction = targetPos - m_position;
            SetupPerspectiveCamera(m_position, direction, Vector3.UnitY);
        }
    }

    // Apocalypse survival core component
    // Phase 2: Top-down camera + WASD movement + mouse facing + wave spawning + kill tracking
    public class SuApocalypseComponent : Component, IUpdateable
    {
        // ========== Wave ==========
        private const int BaseWaveSize = 10;
        private const int WaveSizeIncrement = 10;
        private const int MaxCreatures = 40;
        private const float WaveCheckInterval = 5f;

        // ========== Kill reward ==========
        private const int KillRewardInterval = 10;  // reward every 10 kills
        private const int RewardWeaponCount = 1;
        private const int RewardBlockCount = 16;

        // ========== Movement ==========
        private const float WalkSpeed = 4.3f;
        private const float JumpImpulse = 6f;

        // ========== Subsystems ==========
        private SubsystemGameWidgets m_subsystemGameWidgets;
        private SubsystemTime m_subsystemTime;
        private SubsystemGameInfo m_subsystemGameInfo;
        private SubsystemTerrain m_subsystemTerrain;
        private SubsystemBodies m_subsystemBodies;
        private ComponentPlayer m_componentPlayer;

        // ========== Camera ==========
        private SuTopDownCamera m_topDownCamera;

        // ========== State ==========
        private int m_waveNumber = 0;
        private int m_totalKills = 0;
        private int m_creaturesAlive = 0;
        private float m_waveCheckTimer = 0f;
        private bool m_waveActive = false;
        private bool m_initialized = false;
        private int m_lastCreatureCount = -1;  // for kill detection
        // ========== Mouse → Character Facing (Screen Raycast) ==========
        // SuTopDownCamera: UsesMovementControls=false, IsEntityControlEnabled=true
        // → Mouse not captured, cursor visible, MousePosition available
        // → Move/Look/Jump/Dig/Hit/Interact all preserved (no clearing by ComponentInput)
        // → We must zero Look and TurnOrder to prevent ComponentLocomotion double-rotation
        private float m_targetYaw = 0f;
        private float m_mouseDebugTimer = 0f;

        private void UpdateMouseFacing(float dt)
        {
            var gameWidget = m_componentPlayer.GameWidget;
            if (gameWidget?.ActiveCamera != m_topDownCamera) return;

            var body = m_componentPlayer.ComponentBody;

            // Force mouse cursor visible for top-down mode
            // ComponentInput hides it in UpdateInputFromMouseAndKeyboard,
            // but we re-enable it here (we run after ComponentInput)
            gameWidget.Input.IsMouseCursorVisible = true;

            // Prevent ComponentLocomotion from rotating the character via Look
            // (We control rotation ourselves via raycast)
            // Note: WalkOrder is handled by ComponentLocomotion before us,
            // but we override Velocity horizontal in UpdateMovement.
            // TurnOrder/LookOrder must be zeroed to prevent double-rotation.
            m_componentPlayer.ComponentLocomotion.TurnOrder = Vector2.Zero;
            m_componentPlayer.ComponentLocomotion.LookOrder = Vector2.Zero;

            var input = gameWidget.Input;
            Vector2? mousePosOpt = input.MousePosition;
            if (!mousePosOpt.HasValue) return;

            var cam = gameWidget.ActiveCamera;
            Vector2 mouseScreen = mousePosOpt.Value;

            // ScreenToWorld: Vector3(X, Y, Z) where Z=0 near plane, Z=1 far plane
            Vector3 rayStart = cam.ScreenToWorld(new Vector3(mouseScreen.X, mouseScreen.Y, 0f), Matrix.Identity);
            Vector3 rayEnd = cam.ScreenToWorld(new Vector3(mouseScreen.X, mouseScreen.Y, 1f), Matrix.Identity);

            // Intersect ray with Y = character center plane
            Vector3 charPos = body.Position;
            float groundY = charPos.Y + 0.5f;
            Vector3 rayDir = rayEnd - rayStart;
            float t = (groundY - rayStart.Y) / rayDir.Y;
            Vector3 groundPoint;
            if (t > 0f && t < 1f)
            {
                groundPoint = rayStart + t * rayDir;
            }
            else
            {
                groundPoint = charPos + new Vector3(rayDir.X, 0f, rayDir.Z) * 5f;
            }

            // Face from character to ground point (horizontal only)
            Vector3 faceDir = groundPoint - charPos;
            faceDir.Y = 0f;
            if (faceDir.LengthSquared() > 0.01f)
            {
                m_targetYaw = MathUtils.Atan2(-faceDir.X, -faceDir.Z);
                body.Rotation = Quaternion.CreateFromAxisAngle(Vector3.UnitY, m_targetYaw);
            }
        }

        // Hostile templates — Source: Database.xml EntityTemplate entries
        private static readonly string[] s_hostileTemplates = new[]
        {
            "Wolf", "Wolf_Gray", "Wolf_Coyote", "Hyena",
            "Tiger", "Tiger_White",
            "Lion", "Leopard", "Jaguar",
            "Bear", "Bear_Brown", "Bear_Black", "Bear_Polar"
        };

        // Weapon items for rewards — block indices
        // Source: BlocksManager — Iron Sword=151, Diamond Sword=152, Steel Sword=153
        private static readonly int[] s_weaponBlocks = new[] { 151, 152, 153 };
        // Reward blocks — Stone=2, Iron Ore=6, Diamond=40, Sand=8, Gravel=10
        private static readonly int[] s_rewardBlocks = new[] { 2, 6, 40, 8, 10 };

        // Run at Body order (2) — after ComponentLocomotion (1)
        // We directly set Body.Velocity (horizontal) and Rotation (facing)
        public UpdateOrder UpdateOrder => UpdateOrder.Body;

        protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
        {
            m_subsystemGameWidgets = Project.FindSubsystem<SubsystemGameWidgets>(throwOnError: true);
            m_subsystemTime = Project.FindSubsystem<SubsystemTime>(throwOnError: true);
            m_subsystemGameInfo = Project.FindSubsystem<SubsystemGameInfo>(throwOnError: true);
            m_subsystemTerrain = Project.FindSubsystem<SubsystemTerrain>(throwOnError: true);
            m_subsystemBodies = Project.FindSubsystem<SubsystemBodies>(throwOnError: true);
            m_componentPlayer = Entity.FindComponent<ComponentPlayer>(throwOnError: true);
        }

        void IUpdateable.Update(float dt)
        {
            if (m_componentPlayer.ComponentHealth.Health <= 0f) return;
            if (m_subsystemGameWidgets.GameWidgets.Count == 0) return;

            if (!m_initialized)
            {
                Initialize();
                m_initialized = true;
            }

            UpdateMovement(dt);
            DetectKills();
            UpdateWaveSystem(dt);
        }

        private void Initialize()
        {
            GameWidget gameWidget = m_subsystemGameWidgets.GameWidgets[0];

            // Create and install our custom top-down camera
            m_topDownCamera = new SuTopDownCamera(gameWidget);
            gameWidget.ActiveCamera = m_topDownCamera;

            // Initialize target yaw from current rotation
            Quaternion rot = m_componentPlayer.ComponentBody.Rotation;
            m_targetYaw = MathUtils.Atan2(2f * rot.Y * rot.W - 2f * rot.X * rot.Z, 1f - 2f * rot.Y * rot.Y - 2f * rot.Z * rot.Z);

            Log.Information($"[SuApocalypse] Initialized — SuTopDownCamera installed, initial yaw={m_targetYaw:F3}");
        }

        // ========== Direct Body Control (Top-down Mode) ==========
        // SuTopDownCamera.UsesMovementControls=true → ComponentInput zeros Move/Look
        // CameraMove/CameraLook preserve pre-zeroed copies (ComponentInput lines 87-89)
        // We read CameraMove for WASD input and set Velocity/Rotation directly
        // Source: ComponentInput lines 87-89 — CameraMove=Move, CameraLook=Look (before zero)
        private void UpdateMovement(float dt)
        {
            var playerInput = m_componentPlayer.ComponentInput.PlayerInput;
            Vector3 cameraMove = playerInput.CameraMove;
            var body = m_componentPlayer.ComponentBody;

            // ========== WASD → Horizontal Velocity ==========
            // CameraMove: X=strafe, Y=jump, Z=forward (same as Move before zeroing)
            float forward = cameraMove.Z;  // W=+1, S=-1
            float strafe = cameraMove.X;   // D=+1, A=-1

            if (forward != 0f || strafe != 0f)
            {
                var camera = m_componentPlayer.GameWidget?.ActiveCamera;
                if (camera != null)
                {
                    // Camera forward/right vectors (horizontal only)
                    Vector3 camForward = camera.ViewDirection;
                    camForward.Y = 0f;
                    camForward = camForward.LengthSquared() > 0.001f
                        ? Vector3.Normalize(camForward) : -Vector3.UnitZ;

                    Vector3 camRight = Vector3.Cross(camForward, Vector3.UnitY);
                    camRight = camRight.LengthSquared() > 0.001f
                        ? Vector3.Normalize(camRight) : Vector3.UnitX;

                    Vector3 moveDir = camForward * forward + camRight * strafe;
                    if (moveDir.LengthSquared() > 1f)
                        moveDir = Vector3.Normalize(moveDir);

                    Vector3 horizontalVel = moveDir * WalkSpeed;
                    Vector3 currentVel = body.Velocity;
                    body.Velocity = new Vector3(horizontalVel.X, currentVel.Y, horizontalVel.Z);
                }
            }
            else
            {
                // No input — horizontal friction to stop sliding
                Vector3 vel = body.Velocity;
                float horizontalSpeed = new Vector2(vel.X, vel.Z).Length();
                if (horizontalSpeed > 0.1f)
                {
                    float friction = MathUtils.Max(0f, 1f - 10f * dt);
                    vel.X *= friction;
                    vel.Z *= friction;
                    body.Velocity = vel;
                }
            }

            // ========== Jump ==========
            if (cameraMove.Y > 0.5f && body.StandingOnValue.HasValue)
            {
                Vector3 vel = body.Velocity;
                vel.Y = JumpImpulse;
                body.Velocity = vel;
            }

            // ========== Mouse → Character Facing ==========
            UpdateMouseFacing(dt);
        }

        // ========== Kill Detection ==========
        private void DetectKills()
        {
            CountHostileCreatures();

            if (m_lastCreatureCount >= 0 && m_creaturesAlive < m_lastCreatureCount)
            {
                int killed = m_lastCreatureCount - m_creaturesAlive;
                m_totalKills += killed;

                int prevMilestone = (m_totalKills - killed) / KillRewardInterval;
                int currMilestone = m_totalKills / KillRewardInterval;
                if (currMilestone > prevMilestone)
                {
                    GrantKillReward();
                }
            }
            m_lastCreatureCount = m_creaturesAlive;
        }

        private void GrantKillReward()
        {
            Engine.Random rng = new Engine.Random();
            IInventory inventory = m_componentPlayer.ComponentMiner.Inventory;
            if (inventory == null) return;

            int emptySlot = -1;
            for (int i = 0; i < inventory.SlotsCount; i++)
            {
                if (inventory.GetSlotCount(i) == 0) { emptySlot = i; break; }
            }
            if (emptySlot < 0) return;

            int weaponIdx = rng.Int(0, s_weaponBlocks.Length - 1);
            int weaponBlock = s_weaponBlocks[weaponIdx];
            int weaponValue = Terrain.MakeBlockValue(weaponBlock);
            inventory.AddSlotItems(emptySlot, weaponValue, RewardWeaponCount);

            int blockSlot = -1;
            for (int i = 0; i < inventory.SlotsCount; i++)
            {
                if (inventory.GetSlotCount(i) == 0) { blockSlot = i; break; }
            }
            if (blockSlot >= 0)
            {
                int blockIdx = rng.Int(0, s_rewardBlocks.Length - 1);
                int rewardBlock = s_rewardBlocks[blockIdx];
                int rewardValue = Terrain.MakeBlockValue(rewardBlock);
                inventory.AddSlotItems(blockSlot, rewardValue, RewardBlockCount);
            }

            m_componentPlayer.ComponentGui.DisplaySmallMessage(
                $"Kill reward! +Weapon +{RewardBlockCount} blocks",
                Color.Yellow, blinking: true, playNotificationSound: true);

            Log.Information($"[SuApocalypse] Kill reward granted at {m_totalKills} kills");
        }

        // ========== Wave System ==========
        private void UpdateWaveSystem(float dt)
        {
            m_waveCheckTimer += dt;
            if (m_waveCheckTimer < WaveCheckInterval) return;
            m_waveCheckTimer = 0f;

            if (!m_waveActive)
            {
                StartNewWave();
            }
            else if (m_creaturesAlive <= 0)
            {
                OnWaveCleared();
            }
            else if (m_creaturesAlive < GetCurrentWaveSize() / 2)
            {
                SpawnCreatures(GetCurrentWaveSize() / 2 - m_creaturesAlive);
            }
        }

        private void StartNewWave()
        {
            m_waveNumber++;
            m_waveActive = true;

            int waveSize = GetCurrentWaveSize();
            SpawnCreatures(waveSize);

            m_componentPlayer.ComponentGui.DisplaySmallMessage(
                $"Wave {m_waveNumber}! — {waveSize} enemies",
                Color.Red, blinking: true, playNotificationSound: true);

            Log.Information($"[SuApocalypse] Wave {m_waveNumber} started — {waveSize} enemies");
        }

        private int GetCurrentWaveSize()
        {
            int size = BaseWaveSize + (m_waveNumber - 1) * WaveSizeIncrement;
            return MathUtils.Min(size, MaxCreatures);
        }

        private void OnWaveCleared()
        {
            m_waveActive = false;

            m_componentPlayer.ComponentGui.DisplaySmallMessage(
                $"Wave {m_waveNumber} cleared! Kills: {m_totalKills}",
                Color.Green, blinking: true, playNotificationSound: true);

            Log.Information($"[SuApocalypse] Wave {m_waveNumber} cleared — total kills: {m_totalKills}");
        }

        // ========== Creature Spawning ==========
        private void SpawnCreatures(int count)
        {
            if (count <= 0) return;

            Vector3 playerPos = m_componentPlayer.ComponentBody.Position;
            Engine.Random random = new Engine.Random();

            int spawned = 0;
            int maxAttempts = count * 3;

            for (int i = 0; i < maxAttempts && spawned < count; i++)
            {
                float angle = random.Float(0f, MathUtils.PI * 2f);
                float dist = 16f + random.Float(0f, 16f);
                int cellX = Terrain.ToCell(playerPos.X + MathUtils.Cos(angle) * dist);
                int cellZ = Terrain.ToCell(playerPos.Z + MathUtils.Sin(angle) * dist);

                if (!m_subsystemTerrain.Terrain.IsCellValid(cellX, 0, cellZ)) continue;

                int topY = m_subsystemTerrain.Terrain.GetTopHeight(cellX, cellZ);
                int surfaceValue = m_subsystemTerrain.Terrain.GetCellValueFast(cellX, topY, cellZ);
                int surfaceBlock = Terrain.ExtractContents(surfaceValue);
                Block block = BlocksManager.Blocks[surfaceBlock];

                if (block.IsTransparent || surfaceBlock == 18 || surfaceBlock == 62) continue;

                string templateName = s_hostileTemplates[random.Int(0, s_hostileTemplates.Length - 1)];

                try
                {
                    Entity entity = DatabaseManager.CreateEntity(
                        m_componentPlayer.Entity.Project, templateName, true);
                    if (entity != null)
                    {
                        ComponentBody spawnBody = entity.FindComponent<ComponentBody>(throwOnError: false);
                        if (spawnBody != null)
                        {
                            spawnBody.Position = new Vector3(cellX + 0.5f, topY + 1, cellZ + 0.5f);
                        }
                        m_componentPlayer.Entity.Project.AddEntity(entity);
                        spawned++;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error($"[SuApocalypse] Spawn {templateName} failed: {ex.Message}");
                }
            }
        }

        // ========== Hostile Count ==========
        private void CountHostileCreatures()
        {
            m_creaturesAlive = 0;
            var creatureSpawn = Project.FindSubsystem<SubsystemCreatureSpawn>(throwOnError: false);
            if (creatureSpawn == null) return;

            foreach (ComponentCreature creature in creatureSpawn.Creatures)
            {
                if (creature == null || creature.ComponentHealth.Health <= 0f) continue;

                ComponentChaseBehavior chase = creature.Entity.FindComponent<ComponentChaseBehavior>();
                if (chase != null)
                {
                    m_creaturesAlive++;
                    continue;
                }

                ComponentFindPlayerBehavior findPlayer = creature.Entity.FindComponent<ComponentFindPlayerBehavior>();
                if (findPlayer != null)
                {
                    m_creaturesAlive++;
                }
            }
        }
    }
}
