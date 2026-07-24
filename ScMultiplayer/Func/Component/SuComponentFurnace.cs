using Engine;
using Game;
using GameEntitySystem;
using SuAPI;
using TemplatesDatabase;

namespace ScMultiplayer
{
    public sealed class SuComponentFurnace : ComponentFurnace, IUpdateable
    {
        private ModFieldRef<SuComponentFurnace, float> m_fireTimeRemainingField;
        private ModFieldRef<SuComponentFurnace, float> m_heatLevelField;
        private ModFieldRef<SuComponentFurnace, float> m_smeltingProgressField;
        private ModFieldRef<SuComponentFurnace, bool> m_updateSmeltingRecipeField;
        private ComponentBlockEntity m_blockEntity;

        internal Point3 Coordinates => m_blockEntity?.Coordinates ?? default;

        internal float FireTimeRemaining => m_fireTimeRemainingField(this);

        // Source: Survivalcraft/Game/ComponentFurnace.cs:ComponentFurnace.Load
        protected override void Load(ValuesDictionary valuesDictionary,
            IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            m_fireTimeRemainingField = ScMultiplayer.ModManager.ModParentField
                .BindFieldRef<SuComponentFurnace, float>("m_fireTimeRemaining");
            m_heatLevelField = ScMultiplayer.ModManager.ModParentField
                .BindFieldRef<SuComponentFurnace, float>("m_heatLevel");
            m_smeltingProgressField = ScMultiplayer.ModManager.ModParentField
                .BindFieldRef<SuComponentFurnace, float>("m_smeltingProgress");
            m_updateSmeltingRecipeField = ScMultiplayer.ModManager.ModParentField
                .BindFieldRef<SuComponentFurnace, bool>("m_updateSmeltingRecipe");
            m_blockEntity = Entity.FindComponent<ComponentBlockEntity>(true);
        }

        // Source: Survivalcraft/Game/ComponentFurnace.cs:ComponentFurnace.Update
        void IUpdateable.Update(float dt)
        {
            if (ScMultiplayer.client?.IsConnected == true && !ScMultiplayer.IsHost)
                return;
            base.Update(dt);
        }

        // Source: Survivalcraft/Game/ComponentFurnace.cs:ComponentFurnace.Update
        internal void ApplyNetworkState(float fireTimeRemaining, float heatLevel,
            float smeltingProgress)
        {
            m_fireTimeRemainingField(this) = MathUtils.Max(fireTimeRemaining, 0f);
            m_heatLevelField(this) = MathUtils.Max(heatLevel, 0f);
            m_smeltingProgressField(this) = MathUtils.Clamp(smeltingProgress, 0f, 1f);
            m_updateSmeltingRecipeField(this) = false;
        }
    }
}
