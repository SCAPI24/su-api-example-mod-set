using System;
using System.Collections.Generic;
using Engine;
using Engine.Input;
using Game;
using GameEntitySystem;
using SuAPI;
using TemplatesDatabase;

namespace ControlAnimal;

public class ControlAnimalComponent : Component, IUpdateable
{
    private const float ControlHoldTime = 2f;
    private const float SearchRange = 2.5f;
    private const string ControlButtonName = "ControlAnimal.RButton";
    private const string EggButtonName = "ControlAnimal.EggButton";

    private ComponentPlayer m_player;
    private SubsystemGameWidgets m_gameWidgets;
    private SubsystemBodies m_bodies;
    private SubsystemUpdate m_subsystemUpdate;
    private SubsystemTerrain m_terrain;
    private SubsystemPickables m_pickables;
    private SubsystemAudio m_audio;
    private readonly DynamicArray<ComponentBody> m_nearbyBodies = new DynamicArray<ComponentBody>();
    private readonly List<ComponentBehavior> m_suspendedBehaviors = new List<ComponentBehavior>();
    private readonly List<IUpdateable> m_suspendedUpdateables = new List<IUpdateable>();
    private readonly Dictionary<ComponentModel, float?> m_playerModelOpacities = new Dictionary<ComponentModel, float?>();
    private ControlAnimalButtonWidget m_controlButton;
    private ControlAnimalButtonWidget m_eggButton;
    private ButtonWidget m_creativeFlyButton;
    private ButtonWidget m_crouchButton;
    private ComponentCreature m_nearbyAnimal;
    private ComponentCreature m_controlledAnimal;
    private float m_controlHoldTime;
    private bool m_controlInputWasHeld;
    private bool m_exitInputArmed = true;
    private bool m_isControlling;
    private bool m_originalGravity;
    private bool m_originalGroundDrag;
    private ComponentBody m_pendingAttackBody;
    private Vector3 m_pendingAttackPoint;
    private Vector3 m_pendingAttackDirection;
    private float m_pendingAttackTime;
    private Pickable m_pendingFeedPickable;
    private TerrainRaycastResult? m_pendingFeedTerrain;
    private float m_feedHoldTime;
    private bool m_birdFlightEnabled;
    private bool m_birdLanding;
    private float m_birdTakeoffTime;
    private float m_birdLiftPulseTime;
    private float m_birdLandingLiftPulseTime;
    private EggBlock.EggType m_birdEggType;
    private float m_layEggTime;
    private bool m_isLayingEgg;
    private readonly Game.Random m_random = new Game.Random();

    // Source: Survivalcraft/Game/UpdateOrder.cs:UpdateOrder.Input
    // Input is sampled at -10; this component runs immediately afterwards and before ComponentGui.
    public UpdateOrder UpdateOrder => (UpdateOrder)(-9);

    protected override void Load(ValuesDictionary valuesDictionary, IdToEntityMap idToEntityMap)
    {
        base.Load(valuesDictionary, idToEntityMap);
        m_player = Entity.FindComponent<ComponentPlayer>(throwOnError: true);
        m_gameWidgets = Project.FindSubsystem<SubsystemGameWidgets>(throwOnError: true);
        m_bodies = Project.FindSubsystem<SubsystemBodies>(throwOnError: true);
        m_subsystemUpdate = Project.FindSubsystem<SubsystemUpdate>(throwOnError: true);
        m_terrain = Project.FindSubsystem<SubsystemTerrain>(throwOnError: true);
        m_pickables = Project.FindSubsystem<SubsystemPickables>(throwOnError: true);
        m_audio = Project.FindSubsystem<SubsystemAudio>(throwOnError: true);
    }

    public override void Dispose()
    {
        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.OnEntityRemoved
        // The GameWidget survives player death/respawn, so entity-owned HUD widgets must be detached.
        if (m_isControlling)
        {
            RestoreAnimalBehaviors();
            RestorePlayerModels();
            m_controlledAnimal = null;
            m_isControlling = false;
        }
        DetachControlButtons();
        base.Dispose();
    }

    void IUpdateable.Update(float dt)
    {
        if (m_gameWidgets.GameWidgets.Count == 0)
        {
            return;
        }

        AttachControlButton();
        if (m_controlButton == null)
        {
            return;
        }

        // Source: Survivalcraft/Game/ComponentInput.cs:ComponentInput.UpdateInputFromMouseAndKeyboard
        // Shift remains the native crouch toggle. R and the fixed HUD button are equivalent control inputs.
        WidgetInput widgetInput = m_player.GameWidget.Input;
        bool controlInputHeld = widgetInput.IsKeyDown(Key.R) || m_controlButton.IsPressed;
        PlayerInput playerInput = m_player.ComponentInput.PlayerInput;
        SetPlayerInput(playerInput);

        // ComponentInput maps R to ToggleMount. Clear it before ComponentGui handles input so a long press
        // never starts the game's normal rider/mount path.
        if (playerInput.ToggleMount)
        {
            playerInput.ToggleMount = false;
            SetPlayerInput(playerInput);
        }

        if (m_isControlling)
        {
            UpdateControlledAnimal(playerInput, dt);
            ClearHumanControlInput(playerInput);
            UpdateExitInput(controlInputHeld);
        }
        else
        {
            UpdateControlEntry(dt, controlInputHeld);
        }

        m_controlInputWasHeld = controlInputHeld;
    }

    private void AttachControlButton()
    {
        if (m_controlButton != null)
        {
            return;
        }

        // Source: Pak/Widgets/GameWidget.xml:RightControlsContainer
        // This is an independent, always-present Windows control and does not depend on native mount visibility.
        StackPanelWidget rightControls = m_player.GameWidget.Children.Find<StackPanelWidget>(
            "RightControlsContainer",
            true);
        if (rightControls == null)
        {
            return;
        }

        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.SpawnPlayer
        // Respawn reuses GameWidget. Remove any stale widgets left by an interrupted entity lifecycle.
        RemoveNamedControlButton(rightControls, ControlButtonName);
        RemoveNamedControlButton(rightControls, EggButtonName);

        m_controlButton = new ControlAnimalButtonWidget
        {
            Name = ControlButtonName,
            Size = new Vector2(76f, 64f),
            Margin = new Vector2(0f, 3f),
            HorizontalAlignment = WidgetAlignment.Far,
            Text = "R",
            Color = Color.White,
            CenterColor = new Color(80, 80, 80),
            BevelColor = new Color(160, 160, 160),
            IsAutoCheckingEnabled = false
        };
        rightControls.Children.Add(m_controlButton);
        m_eggButton = new ControlAnimalButtonWidget
        {
            Name = EggButtonName,
            Size = new Vector2(76f, 64f),
            Margin = new Vector2(0f, 3f),
            HorizontalAlignment = WidgetAlignment.Far,
            Text = "EGG",
            Color = Color.White,
            CenterColor = new Color(80, 80, 80),
            BevelColor = new Color(160, 160, 160),
            IsAutoCheckingEnabled = false,
            IsVisible = false
        };
        rightControls.Children.Add(m_eggButton);
        m_creativeFlyButton = ModManager.Instance.ModParentField.GetParentField<ButtonWidget>(
            m_player.ComponentGui,
            "m_creativeFlyButtonWidget",
            typeof(ComponentGui));
        m_crouchButton = ModManager.Instance.ModParentField.GetParentField<ButtonWidget>(
            m_player.ComponentGui,
            "m_crouchButtonWidget",
            typeof(ComponentGui));
    }

    private void DetachControlButtons()
    {
        if (m_controlButton?.ParentWidget != null)
        {
            m_controlButton.ParentWidget.Children.Remove(m_controlButton);
        }
        if (m_eggButton?.ParentWidget != null)
        {
            m_eggButton.ParentWidget.Children.Remove(m_eggButton);
        }
        m_controlButton = null;
        m_eggButton = null;
        m_creativeFlyButton = null;
        m_crouchButton = null;
    }

    private static void RemoveNamedControlButton(ContainerWidget container, string name)
    {
        Widget stale = container.Children.Find<Widget>(name, false);
        if (stale != null)
        {
            container.Children.Remove(stale);
        }
    }

    private void UpdateControlEntry(float dt, bool controlInputHeld)
    {
        m_nearbyAnimal = FindNearbyAnimal();
        bool crouching = m_player.ComponentBody.CrouchFactor > 0.5f ||
            m_player.ComponentBody.TargetCrouchFactor > 0.5f;
        bool canControl = crouching && m_nearbyAnimal != null;

        if (canControl && controlInputHeld)
        {
            m_controlHoldTime = MathUtils.Min(ControlHoldTime, m_controlHoldTime + dt);
            if (m_controlHoldTime >= ControlHoldTime)
            {
                EnterAnimal(m_nearbyAnimal);
                m_exitInputArmed = false;
                return;
            }
        }
        else
        {
            m_controlHoldTime = 0f;
        }

        // Source: Game/BevelledButtonWidget.cs:BevelledButtonWidget.CenterColor
        // The button fills green as the two-second R/button hold progresses.
        float progress = canControl ? m_controlHoldTime / ControlHoldTime : 0f;
        Color idleColor = new Color(80, 80, 80);
        Color readyColor = new Color(40, 150, 70);
        m_controlButton.CenterColor = Color.Lerp(idleColor, readyColor, progress);
        m_controlButton.Color = Color.White;
        m_controlButton.Text = canControl
            ? string.Format("R {0:F1}", MathUtils.Max(0f, ControlHoldTime - m_controlHoldTime))
            : "R";
    }

    private void UpdateExitInput(bool controlInputHeld)
    {
        if (!controlInputHeld)
        {
            m_exitInputArmed = true;
        }
        else if (m_exitInputArmed && !m_controlInputWasHeld)
        {
            LeaveAnimal();
        }
    }

    private ComponentCreature FindNearbyAnimal()
    {
        // Source: Survivalcraft/Game/SubsystemBodies.cs:SubsystemBodies.FindBodiesAroundPoint
        Vector3 playerPosition = m_player.ComponentBody.Position;
        m_nearbyBodies.Clear();
        m_bodies.FindBodiesAroundPoint(playerPosition.XZ, SearchRange, m_nearbyBodies);
        ComponentCreature nearest = null;
        float nearestDistanceSquared = SearchRange * SearchRange;
        for (int i = 0; i < m_nearbyBodies.Count; i++)
        {
            ComponentBody body = m_nearbyBodies.Array[i];
            ComponentCreature creature = body.Entity.FindComponent<ComponentCreature>();
            if (creature == null || creature is ComponentPlayer || creature.ComponentHealth.Health <= 0f)
            {
                continue;
            }
            float distanceSquared = Vector3.DistanceSquared(body.Position, playerPosition);
            if (distanceSquared < nearestDistanceSquared)
            {
                nearest = creature;
                nearestDistanceSquared = distanceSquared;
            }
        }
        return nearest;
    }

    private void EnterAnimal(ComponentCreature animal)
    {
        // Source: Survivalcraft/Game/GameWidget.cs:GameWidget.Target
        m_controlledAnimal = animal;
        m_isControlling = true;
        m_originalGravity = m_player.ComponentBody.IsGravityEnabled;
        m_originalGroundDrag = m_player.ComponentBody.IsGroundDragEnabled;
        m_player.ComponentBody.IsGravityEnabled = false;
        m_player.ComponentBody.IsGroundDragEnabled = false;
        m_player.ComponentBody.Velocity = Vector3.Zero;
        SuspendAnimalBehaviors(animal);
        HidePlayerModels();
        animal.Entity.FindComponent<ComponentPathfinding>()?.Stop();
        m_player.GameWidget.Target = animal;
        m_player.GameWidget.ActiveCamera = m_player.GameWidget.FindCamera<FppCamera>();
        m_player.ComponentBody.TargetCrouchFactor = 0f;
        m_controlHoldTime = 0f;
        m_controlButton.Text = "BACK";
        m_controlButton.Color = Color.White;
        m_controlButton.CenterColor = new Color(40, 150, 70);
        m_birdFlightEnabled = false;
        m_birdLanding = false;
        m_birdTakeoffTime = 0f;
        m_birdLiftPulseTime = 0f;
        m_birdLandingLiftPulseTime = 0f;
        m_isLayingEgg = false;
        m_layEggTime = 0f;
        EggBlock eggBlock = (EggBlock)BlocksManager.Blocks[EggBlock.Index];
        m_birdEggType = IsBird(animal)
            ? eggBlock.GetEggTypeByCreatureTemplateName(
                animal.Entity.ValuesDictionary.DatabaseObject.Name)
            : null;
    }

    private void LeaveAnimal()
    {
        // Source: Survivalcraft/Game/PlayerData.cs:PlayerData.OnEntityAdded
        m_isControlling = false;
        RestoreAnimalBehaviors();
        RestorePlayerModels();
        MovePlayerBesideAnimal();
        m_controlledAnimal = null;
        m_player.GameWidget.Target = m_player;
        m_player.GameWidget.ActiveCamera = m_player.GameWidget.FindCamera<FppCamera>();
        m_player.ComponentBody.IsGravityEnabled = m_originalGravity;
        m_player.ComponentBody.IsGroundDragEnabled = m_originalGroundDrag;
        m_player.ComponentBody.Velocity = Vector3.Zero;
        m_controlHoldTime = 0f;
        m_pendingAttackBody = null;
        m_pendingAttackTime = 0f;
        CancelAnimalFeeding();
        m_birdFlightEnabled = false;
        m_birdLanding = false;
        m_birdLiftPulseTime = 0f;
        m_birdLandingLiftPulseTime = 0f;
        m_birdEggType = null;
        m_isLayingEgg = false;
        m_layEggTime = 0f;
        if (m_eggButton != null)
        {
            m_eggButton.IsVisible = false;
        }
        m_controlButton.Text = "R";
        m_controlButton.Color = Color.White;
        m_controlButton.CenterColor = new Color(80, 80, 80);
        if (m_player.ComponentGui.ModalPanelWidget is AnimalStatsWidget)
        {
            m_player.ComponentGui.ModalPanelWidget = null;
        }
    }

    private void UpdateControlledAnimal(PlayerInput input, float dt)
    {
        if (m_controlledAnimal == null || !m_controlledAnimal.IsAddedToProject ||
            m_controlledAnimal.ComponentHealth.Health <= 0f)
        {
            LeaveAnimal();
            return;
        }

        // Source: Survivalcraft/Game/ComponentPlayer.cs:ComponentPlayer.Update
        ComponentLocomotion locomotion = m_controlledAnimal.ComponentLocomotion;
        float sideInput = MathUtils.Abs(input.Move.X) > 0.001f ? input.Move.X : input.CrouchMove.X;
        float forwardInput = MathUtils.Abs(input.Move.Z) > 0.001f ? input.Move.Z : input.CrouchMove.Z;
        bool isBird = IsBird(m_controlledAnimal);
        bool canFlyBird = IsControllableBird(m_controlledAnimal);
        HandleReusedAnimalControls(ref input, canFlyBird);
        UpdateBirdFlight(locomotion, input, forwardInput, canFlyBird, dt);
        locomotion.WalkOrder = canFlyBird && (m_birdFlightEnabled || m_birdLanding)
            ? null
            : new Vector2(0f, forwardInput);
        locomotion.TurnOrder = new Vector2(sideInput, 0f);
        ApplyAnimalLookOrder(locomotion, input.Look, sideInput);
        // Source: Survivalcraft/Game/ComponentInput.cs:UpdateInputFromWidgets
        // Android movement-pad taps produce a one-frame Jump edge. On ground it remains a native
        // bird jump; in flight UpdateBirdFlight converts each edge into one lift pulse.
        locomotion.JumpOrder = canFlyBird && (m_birdFlightEnabled || m_birdLanding)
            ? 0f
            : (input.Jump ? 1f : 0f);
        m_player.ComponentBody.Velocity = Vector3.Zero;
        m_player.ComponentLocomotion.WalkOrder = Vector2.Zero;
        m_player.ComponentLocomotion.TurnOrder = Vector2.Zero;
        m_player.ComponentLocomotion.JumpOrder = 0f;

        // Source: Survivalcraft/Game/ComponentChaseBehavior.cs:ComponentChaseBehavior.Update
        // AttackOrder starts the native animal animation. Damage is applied only at IsAttackHitMoment.
        ResolvePendingAttack(dt);
        if (input.Hit.HasValue)
        {
            TryStartAnimalAttack(CreateAnimalInteractionRay());
        }
        if (input.Dig.HasValue)
        {
            UpdateAnimalFeeding(CreateAnimalInteractionRay(), dt);
        }
        else
        {
            CancelAnimalFeeding();
        }
        // Source: Survivalcraft/Game/ComponentInput.cs:UpdateInputFromMouseAndKeyboard
        // Use the physical right-mouse edge instead of generic Interact, because Android taps set
        // both Hit and Interact and must remain normal bird attacks.
        bool rightMouseClicked = m_player.GameWidget.Input.IsMouseButtonDownOnce(MouseButton.Right);
        if (isBird && (rightMouseClicked || m_eggButton?.IsClicked == true))
        {
            TryStartLayingEgg();
            if (rightMouseClicked)
            {
                input.Interact = null;
            }
        }
        UpdateLayingEgg(dt);

        // Source: Survivalcraft/Game/ClothingWidget.cs:ClothingWidget.Update
        if (m_player.ComponentGui.ModalPanelWidget is not AnimalStatsWidget &&
            (m_player.ComponentGui.ModalPanelWidget is ClothingWidget ||
            m_player.ComponentGui.ModalPanelWidget is VitalStatsWidget))
        {
            m_player.ComponentGui.ModalPanelWidget =
                new AnimalStatsWidget(m_player, m_controlledAnimal);
        }
    }

    public void UpdateReusedControlButtons()
    {
        if (!m_isControlling)
        {
            return;
        }
        bool isBird = IsBird(m_controlledAnimal);
        bool canFlyBird = IsControllableBird(m_controlledAnimal);
        if (m_creativeFlyButton != null)
        {
            m_creativeFlyButton.IsVisible = canFlyBird;
            m_creativeFlyButton.IsChecked = canFlyBird && m_birdFlightEnabled;
        }
        if (m_crouchButton != null)
        {
            m_crouchButton.IsVisible = true;
            m_crouchButton.IsChecked = false;
        }
        if (m_eggButton != null)
        {
            m_eggButton.IsVisible = isBird && m_birdEggType != null;
            m_eggButton.CenterColor = m_isLayingEgg
                ? new Color(40, 150, 70)
                : new Color(80, 80, 80);
            m_eggButton.Text = m_isLayingEgg
                ? string.Format("EGG {0:F1}", MathUtils.Max(0f, 3f - m_layEggTime))
                : "EGG";
        }
    }

    private void HandleReusedAnimalControls(ref PlayerInput input, bool isBird)
    {
        bool flyClicked = m_creativeFlyButton?.IsClicked == true || input.ToggleCreativeFly;
        if (flyClicked)
        {
            if (isBird)
            {
                m_birdFlightEnabled = !m_birdFlightEnabled;
                m_birdLanding = !m_birdFlightEnabled;
                m_birdTakeoffTime = m_birdFlightEnabled ? 0.45f : 0f;
                m_birdLiftPulseTime = 0f;
            }
            input.ToggleCreativeFly = false;
            ConsumeButtonClick(m_creativeFlyButton);
        }
        bool skillClicked = m_crouchButton?.IsClicked == true || input.ToggleCrouch;
        if (skillClicked)
        {
            m_controlledAnimal.ComponentCreatureSounds.PlayIdleSound(skipIfRecentlyPlayed: false);
            input.ToggleCrouch = false;
            ConsumeButtonClick(m_crouchButton);
        }
    }

    private void UpdateBirdFlight(ComponentLocomotion locomotion, PlayerInput input,
        float forwardInput, bool isBird, float dt)
    {
        if (!isBird)
        {
            m_birdFlightEnabled = false;
            m_birdLanding = false;
            return;
        }
        if (m_birdFlightEnabled)
        {
            if (input.Jump)
            {
                // Each desktop/Android jump edge grants one bounded climb impulse. Holding the
                // movement area is not required and cannot create unlimited continuous ascent.
                m_birdLiftPulseTime = 0.70f;
            }
            m_birdTakeoffTime = MathUtils.Max(m_birdTakeoffTime - dt, 0f);
            m_birdLiftPulseTime = MathUtils.Max(m_birdLiftPulseTime - dt, 0f);
            // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.NormalMovement
            // Active flight holds altitude between jump taps. Each tap grants a doubled-duration
            // climb pulse. Descent is allowed only after flight is switched off.
            bool liftActive = m_birdTakeoffTime > 0f || m_birdLiftPulseTime > 0f;
            float vertical = liftActive ? 1f : 0f;
            Vector3 order = forwardInput * m_controlledAnimal.ComponentBody.Matrix.Forward +
                vertical * Vector3.UnitY;
            locomotion.FlyOrder = order;
            if (!liftActive && m_controlledAnimal.ComponentBody.Velocity.Y < 0f)
            {
                Vector3 velocity = m_controlledAnimal.ComponentBody.Velocity;
                velocity.Y = 0f;
                m_controlledAnimal.ComponentBody.Velocity = velocity;
            }
            m_controlledAnimal.ComponentBody.IsGravityEnabled = false;
            m_controlledAnimal.ComponentBody.IsGroundDragEnabled = false;
        }
        else if (m_birdLanding && !m_controlledAnimal.ComponentBody.StandingOnValue.HasValue)
        {
            // Source: Survivalcraft/Game/ComponentInput.cs:UpdateInputFromWidgets
            // During descent, a tap creates a short upward FlyOrder pulse. JumpOrder only works
            // while standing on terrain, so it cannot be used to slow an airborne slide.
            if (input.Jump)
            {
                m_birdLandingLiftPulseTime = 0.35f;
            }
            m_birdLandingLiftPulseTime = MathUtils.Max(m_birdLandingLiftPulseTime - dt, 0f);
            float vertical = m_birdLandingLiftPulseTime > 0f ? 1f : -0.30f;
            // Source: Survivalcraft/Game/ComponentPilot.cs:ComponentPilot.Update
            // Keep steering during controlled descent instead of forcing a vertical-only landing.
            // A/D continues to rotate the body through TurnOrder; W/S supplies forward/back motion.
            locomotion.FlyOrder =
                forwardInput * m_controlledAnimal.ComponentBody.Matrix.Forward +
                new Vector3(0f, vertical, 0f);
            m_controlledAnimal.ComponentBody.IsGravityEnabled = false;
            m_controlledAnimal.ComponentBody.IsGroundDragEnabled = false;
        }
        else if (!m_birdLanding)
        {
            m_birdLandingLiftPulseTime = 0f;
        }
        else
        {
            m_birdLanding = false;
        }
    }

    private static bool IsBird(ComponentCreature animal)
    {
        return animal?.ComponentCreatureModel is ComponentBirdModel ||
            animal?.ComponentCreatureModel is ComponentFlightlessBirdModel;
    }

    private static bool IsControllableBird(ComponentCreature animal)
    {
        return animal?.ComponentCreatureModel is ComponentBirdModel &&
            animal.ComponentLocomotion.FlySpeed > 0f;
    }

    private void TryStartLayingEgg()
    {
        if (m_isLayingEgg || m_birdEggType == null ||
            !m_controlledAnimal.ComponentBody.StandingOnValue.HasValue)
        {
            return;
        }
        m_isLayingEgg = true;
        m_layEggTime = 0f;
    }

    private void UpdateLayingEgg(float dt)
    {
        if (!m_isLayingEgg)
        {
            return;
        }
        if (m_birdEggType == null ||
            !m_controlledAnimal.ComponentBody.StandingOnValue.HasValue)
        {
            m_isLayingEgg = false;
            m_layEggTime = 0f;
            return;
        }
        m_layEggTime += dt;
        m_controlledAnimal.ComponentCreatureModel.HeadShakeOrder = 0.2f;
        if (m_layEggTime < 3f)
        {
            return;
        }
        int value = Terrain.MakeBlockValue(
            EggBlock.Index,
            0,
            EggBlock.SetIsLaid(
                EggBlock.SetEggType(0, m_birdEggType.EggTypeIndex),
                isLaid: true));
        Matrix matrix = m_controlledAnimal.ComponentBody.Matrix;
        Vector3 position = m_controlledAnimal.ComponentBody.BoundingBox.Center();
        Vector3 velocity = 3f * Vector3.Normalize(
            -matrix.Forward + 0.1f * matrix.Up +
            0.2f * m_random.Float(-1f, 1f) * matrix.Right);
        m_pickables.AddPickable(value, 1, position, velocity, null);
        m_audio.PlaySound("Audio/EggLaid", 1f, m_random.Float(-0.1f, 0.1f),
            position, 2f, autoDelay: true);
        m_isLayingEgg = false;
        m_layEggTime = 0f;
    }

    private static void ConsumeButtonClick(ButtonWidget button)
    {
        if (button is not BitmapButtonWidget)
        {
            return;
        }
        ClickableWidget clickable = ModManager.Instance.ModParentField.GetParentField<ClickableWidget>(
            button,
            "m_clickableWidget",
            typeof(BitmapButtonWidget));
        if (clickable != null)
        {
            ModManager.Instance.ModParentField.ModifyParentField(
                clickable,
                "<IsClicked>k__BackingField",
                false,
                typeof(ClickableWidget));
        }
    }

    private Ray3 CreateAnimalInteractionRay()
    {
        // Source: Survivalcraft/Game/TppCamera.cs:TppCamera.Update
        // Third-person input rays start behind the target and fail ComponentMiner's eye-distance
        // filter. Always originate creature interactions at the controlled animal's own eyes.
        ComponentCreatureModel model = m_controlledAnimal.ComponentCreatureModel;
        Matrix eyeMatrix = Matrix.CreateFromQuaternion(model.EyeRotation);
        return new Ray3(model.EyePosition, Vector3.Normalize(eyeMatrix.Forward));
    }

    private void TryStartAnimalAttack(Ray3 ray)
    {
        if (m_pendingAttackBody != null)
        {
            return;
        }
        ComponentMiner miner = m_controlledAnimal.Entity.FindComponent<ComponentMiner>();
        if (miner == null)
        {
            return;
        }
        BodyRaycastResult? hit = miner.Raycast<BodyRaycastResult>(ray, RaycastMode.Interaction);
        if (!hit.HasValue || hit.Value.Distance > 1.75f)
        {
            return;
        }
        Entity targetEntity = hit.Value.ComponentBody.Entity;
        bool isPlayerOrAnimal = targetEntity.FindComponent<ComponentPlayer>() != null ||
            targetEntity.FindComponent<ComponentCreature>() != null;
        if (!isPlayerOrAnimal)
        {
            return;
        }
        m_pendingAttackBody = hit.Value.ComponentBody;
        m_pendingAttackPoint = hit.Value.HitPoint();
        m_pendingAttackDirection = ray.Direction;
        m_pendingAttackTime = 0f;
        if (m_controlledAnimal.ComponentCreatureModel is ComponentBirdModel)
        {
            m_controlledAnimal.ComponentCreatureModel.FeedOrder = true;
        }
        else
        {
            m_controlledAnimal.ComponentCreatureModel.AttackOrder = true;
        }
    }

    private void ResolvePendingAttack(float dt)
    {
        if (m_pendingAttackBody == null)
        {
            return;
        }
        m_pendingAttackTime += dt;
        bool isBird = m_controlledAnimal.ComponentCreatureModel is ComponentBirdModel;
        if (isBird)
        {
            m_controlledAnimal.ComponentCreatureModel.FeedOrder = true;
        }
        bool attackFrameReached = m_controlledAnimal.ComponentCreatureModel.IsAttackHitMoment;
        float fallbackAttackTime = isBird ? 0.35f : 0.75f;
        if (!attackFrameReached && m_pendingAttackTime < fallbackAttackTime)
        {
            return;
        }
        ComponentMiner miner = m_controlledAnimal.Entity.FindComponent<ComponentMiner>();
        if (miner != null && m_pendingAttackBody.IsAddedToProject)
        {
            float distance = Vector3.Distance(
                m_controlledAnimal.ComponentCreatureModel.EyePosition,
                m_pendingAttackBody.BoundingBox.Center());
            if (distance <= 2.0f)
            {
                miner.Hit(m_pendingAttackBody, m_pendingAttackPoint, m_pendingAttackDirection);
                m_controlledAnimal.ComponentCreatureSounds.PlayAttackSound();
            }
        }
        m_pendingAttackBody = null;
        m_pendingAttackTime = 0f;
    }

    private void UpdateAnimalFeeding(Ray3 ray, float dt)
    {
        if (m_pendingAttackBody != null)
        {
            CancelAnimalFeeding();
            return;
        }
        if (!TryFindFeedTarget(ray, out Pickable pickable, out TerrainRaycastResult? terrain))
        {
            CancelAnimalFeeding();
            return;
        }
        bool sameTarget = pickable != null
            ? m_pendingFeedPickable == pickable
            : m_pendingFeedTerrain.HasValue && terrain.HasValue &&
                m_pendingFeedTerrain.Value.CellFace.Point == terrain.Value.CellFace.Point;
        if (!sameTarget)
        {
            m_pendingFeedPickable = pickable;
            m_pendingFeedTerrain = terrain;
            m_feedHoldTime = 0f;
        }
        m_feedHoldTime += dt;
        m_controlledAnimal.ComponentCreatureModel.FeedOrder = true;
        if (m_feedHoldTime >= 1f)
        {
            ConsumeFeedTarget(pickable, terrain);
            CancelAnimalFeeding();
        }
    }

    private bool TryFindFeedTarget(Ray3 ray, out Pickable pickable, out TerrainRaycastResult? terrain)
    {
        pickable = null;
        terrain = null;
        Vector3 start = ray.Position;
        Vector3 direction = Vector3.Normalize(ray.Direction);
        float bestDistance = 1.75f;
        foreach (Pickable candidate in m_pickables.Pickables)
        {
            Block block = BlocksManager.Blocks[Terrain.ExtractContents(candidate.Value)];
            if (!IsAnimalFood(block, candidate.Value))
            {
                continue;
            }
            Vector3 offset = candidate.Position - start;
            float distance = Vector3.Dot(offset, direction);
            if (distance <= 0f || distance > bestDistance)
            {
                continue;
            }
            Vector3 closest = start + distance * direction;
            if (Vector3.DistanceSquared(closest, candidate.Position) > 0.36f)
            {
                continue;
            }
            pickable = candidate;
            bestDistance = distance;
        }
        ComponentMiner miner = m_controlledAnimal.Entity.FindComponent<ComponentMiner>();
        TerrainRaycastResult? terrainHit = miner?.Raycast<TerrainRaycastResult>(ray, RaycastMode.Digging);
        if (terrainHit.HasValue && terrainHit.Value.Distance <= bestDistance)
        {
            Block block = BlocksManager.Blocks[Terrain.ExtractContents(terrainHit.Value.Value)];
            if (IsAnimalFood(block, terrainHit.Value.Value))
            {
                pickable = null;
                terrain = terrainHit;
            }
        }
        return pickable != null || terrain.HasValue;
    }

    private static bool IsAnimalFood(Block block, int value)
    {
        int contents = Terrain.ExtractContents(value);
        if (contents == GrassBlock.Index || contents == TallGrassBlock.Index)
        {
            return true;
        }
        return block.FoodType == FoodType.Grass || block.FoodType == FoodType.Meat;
    }

    private void ConsumeFeedTarget(Pickable pickable, TerrainRaycastResult? terrain)
    {
        if (pickable != null)
        {
            pickable.Count = MathUtils.Max(pickable.Count - 1, 0);
            if (pickable.Count == 0)
            {
                pickable.ToRemove = true;
            }
            AddAnimalSatiation();
            return;
        }
        if (!terrain.HasValue)
        {
            return;
        }
        Point3 point = terrain.Value.CellFace.Point;
        int contents = Terrain.ExtractContents(terrain.Value.Value);
        if (contents == GrassBlock.Index)
        {
            m_terrain.ChangeCell(point.X, point.Y, point.Z, Terrain.MakeBlockValue(2, 0, 0));
        }
        else if (contents == TallGrassBlock.Index)
        {
            m_terrain.ChangeCell(point.X, point.Y, point.Z, Terrain.MakeBlockValue(0));
        }
        else
        {
            m_terrain.ChangeCell(point.X, point.Y, point.Z, Terrain.MakeBlockValue(0));
        }
        AddAnimalSatiation();
    }

    private void AddAnimalSatiation()
    {
        ComponentEatPickableBehavior eatBehavior =
            m_controlledAnimal.Entity.FindComponent<ComponentEatPickableBehavior>();
        if (eatBehavior != null)
        {
            float satiation = ModManager.Instance.ModParentField.GetParentField<float>(
                eatBehavior,
                "m_satiation",
                typeof(ComponentEatPickableBehavior));
            ModManager.Instance.ModParentField.ModifyParentField(
                eatBehavior,
                "m_satiation",
                MathUtils.Min(satiation + 1f, 1f),
                typeof(ComponentEatPickableBehavior));
        }
    }

    private void CancelAnimalFeeding()
    {
        m_pendingFeedPickable = null;
        m_pendingFeedTerrain = null;
        m_feedHoldTime = 0f;
    }

    private void ApplyAnimalLookOrder(ComponentLocomotion locomotion, Vector2 mouseLook, float sideInput)
    {
        // Source: Survivalcraft/Game/ComponentLocomotion.cs:ComponentLocomotion.LookAngles
        // LookAngles is the animal head angle relative to its body. Mouse look is clamped here,
        // while sideInput turns the body and recenters the head so the mouse can keep moving.
        float dt = MathUtils.Max(Time.FrameDuration, 0.001f);
        float lookSpeed = MathUtils.Max(locomotion.LookSpeed, 0.001f);
        float maxHeadYaw = MathUtils.DegToRad(140f);
        Vector2 current = locomotion.LookAngles;
        float targetYaw = current.X + lookSpeed * mouseLook.X * dt;
        if (MathUtils.Abs(sideInput) > 0.001f)
        {
            targetYaw *= MathUtils.Max(0f, 1f - 8f * dt);
        }
        targetYaw = MathUtils.Clamp(targetYaw, -maxHeadYaw, maxHeadYaw);
        float targetPitch = MathUtils.Clamp(
            current.Y + lookSpeed * mouseLook.Y * dt,
            -MathUtils.DegToRad(82f),
            MathUtils.DegToRad(82f));
        locomotion.LookOrder = new Vector2(
            (targetYaw - current.X) / (lookSpeed * dt),
            (targetPitch - current.Y) / (lookSpeed * dt));
    }

    private void SuspendAnimalBehaviors(ComponentCreature animal)
    {
        // Source: Survivalcraft/Game/SubsystemUpdate.cs:SubsystemUpdate.RemoveUpdateable
        m_suspendedBehaviors.Clear();
        foreach (ComponentBehavior behavior in animal.Entity.FindComponents<ComponentBehavior>())
        {
            if (behavior.IsActive)
            {
                m_suspendedBehaviors.Add(behavior);
            }
            behavior.IsActive = false;
        }
        m_suspendedUpdateables.Clear();
        foreach (IUpdateable updateable in animal.Entity.FindComponents<IUpdateable>())
        {
            // Source: Survivalcraft/Game/ComponentPilot.cs:ComponentPilot.Update
            // Pilot keeps writing FlyOrder even after behavior AI is disabled. Suspend both the
            // path planner and pilot so only ControlAnimal owns movement while transformed.
            if (updateable is ComponentBehavior ||
                updateable is ComponentBehaviorSelector ||
                updateable is ComponentPathfinding ||
                updateable is ComponentPilot)
            {
                m_suspendedUpdateables.Add(updateable);
                m_subsystemUpdate.RemoveUpdateable(updateable);
            }
        }
    }

    private void RestoreAnimalBehaviors()
    {
        // Source: Survivalcraft/Game/SubsystemUpdate.cs:SubsystemUpdate.AddUpdateable
        if (m_controlledAnimal == null)
        {
            m_suspendedBehaviors.Clear();
            m_suspendedUpdateables.Clear();
            return;
        }
        foreach (IUpdateable updateable in m_suspendedUpdateables)
        {
            m_subsystemUpdate.AddUpdateable(updateable);
        }
        m_suspendedUpdateables.Clear();
        foreach (ComponentBehavior behavior in m_suspendedBehaviors)
        {
            behavior.IsActive = true;
        }
        m_suspendedBehaviors.Clear();
    }

    private void HidePlayerModels()
    {
        // Source: Survivalcraft/Game/ComponentModel.cs:ComponentModel.Opacity
        m_playerModelOpacities.Clear();
        foreach (ComponentModel model in m_player.Entity.FindComponents<ComponentModel>())
        {
            m_playerModelOpacities[model] = model.Opacity;
            model.Opacity = 0f;
        }
    }

    private void RestorePlayerModels()
    {
        foreach (KeyValuePair<ComponentModel, float?> item in m_playerModelOpacities)
        {
            item.Key.Opacity = item.Value;
        }
        m_playerModelOpacities.Clear();
    }

    private void MovePlayerBesideAnimal()
    {
        // Source: Survivalcraft/Game/ComponentFrame.cs:ComponentFrame.Position
        if (m_controlledAnimal == null)
        {
            return;
        }
        ComponentBody animalBody = m_controlledAnimal.ComponentBody;
        float distance = 0.5f * (animalBody.BoxSize.X + m_player.ComponentBody.BoxSize.X) + 0.25f;
        m_player.ComponentBody.Position = animalBody.Position + distance * animalBody.Matrix.Right;
        m_player.ComponentBody.Rotation = animalBody.Rotation;
    }

    private void ClearHumanControlInput(PlayerInput input)
    {
        input.Move = Vector3.Zero;
        input.CrouchMove = Vector3.Zero;
        input.Look = Vector2.Zero;
        input.Jump = false;
        input.Hit = null;
        input.Dig = null;
        input.Aim = null;
        input.Interact = null;
        input.ToggleCrouch = false;
        input.ToggleMount = false;
        input.ToggleCreativeFly = false;
        SetPlayerInput(input);
    }

    private void SetPlayerInput(PlayerInput input)
    {
        // Source: EntitySystem/SuAPI/IModParentField.cs:IModParentField.ModifyParentField
        ModManager.Instance.ModParentField.ModifyParentField(
            m_player.ComponentInput,
            "m_playerInput",
            input,
            typeof(ComponentInput));
    }
}
