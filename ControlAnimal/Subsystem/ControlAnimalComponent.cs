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

    private ComponentPlayer m_player;
    private SubsystemGameWidgets m_gameWidgets;
    private SubsystemBodies m_bodies;
    private SubsystemUpdate m_subsystemUpdate;
    private SubsystemTerrain m_terrain;
    private SubsystemPickables m_pickables;
    private readonly DynamicArray<ComponentBody> m_nearbyBodies = new DynamicArray<ComponentBody>();
    private readonly List<ComponentBehavior> m_suspendedBehaviors = new List<ComponentBehavior>();
    private readonly Dictionary<ComponentModel, float?> m_playerModelOpacities = new Dictionary<ComponentModel, float?>();
    private ControlAnimalButtonWidget m_controlButton;
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

        m_controlButton = new ControlAnimalButtonWidget
        {
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
        locomotion.WalkOrder = new Vector2(0f, forwardInput);
        locomotion.TurnOrder = new Vector2(sideInput, 0f);
        ApplyAnimalLookOrder(locomotion, input.Look, sideInput);
        locomotion.JumpOrder = input.Jump ? 1f : 0f;
        m_player.ComponentBody.Velocity = Vector3.Zero;
        m_player.ComponentLocomotion.WalkOrder = Vector2.Zero;
        m_player.ComponentLocomotion.TurnOrder = Vector2.Zero;
        m_player.ComponentLocomotion.JumpOrder = 0f;

        // Source: Survivalcraft/Game/ComponentChaseBehavior.cs:ComponentChaseBehavior.Update
        // AttackOrder starts the native animal animation. Damage is applied only at IsAttackHitMoment.
        ResolvePendingAttack(dt);
        if (input.Hit.HasValue)
        {
            TryStartAnimalAttack(input.Hit.Value);
        }
        if (input.Dig.HasValue)
        {
            UpdateAnimalFeeding(input.Dig.Value, dt);
        }
        else
        {
            CancelAnimalFeeding();
        }

        // Source: Survivalcraft/Game/ClothingWidget.cs:ClothingWidget.Update
        if (m_player.ComponentGui.ModalPanelWidget is ClothingWidget ||
            m_player.ComponentGui.ModalPanelWidget is VitalStatsWidget)
        {
            m_player.ComponentGui.ModalPanelWidget = new AnimalStatsWidget(m_controlledAnimal);
        }
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
        m_controlledAnimal.ComponentCreatureModel.AttackOrder = true;
    }

    private void ResolvePendingAttack(float dt)
    {
        if (m_pendingAttackBody == null)
        {
            return;
        }
        m_pendingAttackTime += dt;
        bool attackFrameReached = m_controlledAnimal.ComponentCreatureModel.IsAttackHitMoment;
        if (!attackFrameReached && m_pendingAttackTime < 0.75f)
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
        foreach (IUpdateable updateable in animal.Entity.FindComponents<IUpdateable>())
        {
            if (updateable is ComponentBehavior || updateable is ComponentBehaviorSelector)
            {
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
            return;
        }
        foreach (IUpdateable updateable in m_controlledAnimal.Entity.FindComponents<IUpdateable>())
        {
            if (updateable is ComponentBehavior || updateable is ComponentBehaviorSelector)
            {
                m_subsystemUpdate.AddUpdateable(updateable);
            }
        }
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
