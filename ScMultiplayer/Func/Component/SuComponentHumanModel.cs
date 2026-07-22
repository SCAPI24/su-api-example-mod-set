using Engine;
using Engine.Graphics;
using Engine.Media;
using Game;
using GameEntitySystem;
using TemplatesDatabase;

namespace ScMultiplayer
{
    public class SuComponentHumanModel : ComponentHumanModel
    {
        private SubsystemTerrain m_subsystemTerrain;
        private SubsystemModelsRenderer m_subsystemModelsRenderer;
        private ComponentMiner m_componentMiner;
        private ComponentPlayer m_componentPlayer;
        private ModelBone m_hand2Bone;
        private readonly DrawBlockEnvironmentData m_drawBlockEnvironmentData =
            new DrawBlockEnvironmentData();
        private Vector3 m_inHandItemOffset;
        private Vector3 m_inHandItemRotation;

        protected override void Load(ValuesDictionary valuesDictionary,
            IdToEntityMap idToEntityMap)
        {
            base.Load(valuesDictionary, idToEntityMap);
            m_subsystemTerrain = Project.FindSubsystem<SubsystemTerrain>(true);
            m_subsystemModelsRenderer = Project.FindSubsystem<SubsystemModelsRenderer>(true);
            m_componentMiner = Entity.FindComponent<ComponentMiner>();
            m_componentPlayer = Entity.FindComponent<ComponentPlayer>();
            m_hand2Bone = ScMultiplayer.ModManager.ModParentField.GetParentField<ModelBone>(
                this, "m_hand2Bone", typeof(ComponentHumanModel));
        }

        public override void Update(float dt)
        {
            m_inHandItemOffset = Vector3.Lerp(m_inHandItemOffset,
                InHandItemOffsetOrder, 10f * dt);
            m_inHandItemRotation = Vector3.Lerp(m_inHandItemRotation,
                InHandItemRotationOrder, 10f * dt);
            base.Update(dt);
        }

        // Source: Survivalcraft/Game/ComponentHumanModel.cs:ComponentHumanModel.DrawExtras
        public override void DrawExtras(Camera camera)
        {
            if (m_componentCreature.ComponentHealth.Health > 0f &&
                m_componentMiner != null && m_componentMiner.ActiveBlockValue != 0 &&
                m_hand2Bone != null)
            {
                int contents = Terrain.ExtractContents(m_componentMiner.ActiveBlockValue);
                Block block = BlocksManager.Blocks[contents];
                Matrix hand = AbsoluteBoneTransformsForCamera[m_hand2Bone.Index];
                hand *= camera.InvertedViewMatrix;
                hand.Right = Vector3.Normalize(hand.Right);
                hand.Up = Vector3.Normalize(hand.Up);
                hand.Forward = Vector3.Normalize(hand.Forward);
                Matrix item = Matrix.CreateRotationY(
                        MathUtils.DegToRad(block.InHandRotation.Y) + m_inHandItemRotation.Y) *
                    Matrix.CreateRotationZ(
                        MathUtils.DegToRad(block.InHandRotation.Z) + m_inHandItemRotation.Z) *
                    Matrix.CreateRotationX(
                        MathUtils.DegToRad(block.InHandRotation.X) + m_inHandItemRotation.X) *
                    Matrix.CreateTranslation(block.InHandOffset + m_inHandItemOffset) *
                    Matrix.CreateTranslation(new Vector3(0.05f, 0.05f, -0.56f) *
                        (m_componentCreature.ComponentBody.BoxSize.Y / 1.77f)) * hand;
                int x = Terrain.ToCell(item.Translation.X);
                int y = Terrain.ToCell(item.Translation.Y);
                int z = Terrain.ToCell(item.Translation.Z);
                m_drawBlockEnvironmentData.DrawBlockMode = DrawBlockMode.ThirdPerson;
                m_drawBlockEnvironmentData.InWorldMatrix = item;
                m_drawBlockEnvironmentData.Humidity =
                    m_subsystemTerrain.Terrain.GetSeasonalHumidity(x, z);
                m_drawBlockEnvironmentData.Temperature =
                    m_subsystemTerrain.Terrain.GetSeasonalTemperature(x, z) +
                    SubsystemWeather.GetTemperatureAdjustmentAtHeight(y);
                m_drawBlockEnvironmentData.Light =
                    m_subsystemTerrain.Terrain.GetCellLight(x, y, z);
                m_drawBlockEnvironmentData.EnvironmentTemperature =
                    m_componentPlayer?.ComponentVitalStats?.EnvironmentTemperature ?? 12f;
                m_drawBlockEnvironmentData.BillboardDirection = -Vector3.UnitZ;
                m_drawBlockEnvironmentData.SubsystemTerrain = m_subsystemTerrain;
                Matrix viewItem = item * camera.ViewMatrix;
                block.DrawBlock(m_subsystemModelsRenderer.PrimitivesRenderer,
                    m_componentMiner.ActiveBlockValue, Color.White, block.InHandScale,
                    ref viewItem, m_drawBlockEnvironmentData);
            }
            DrawPlayerName(camera);
        }

        private void DrawPlayerName(Camera camera)
        {
            if (m_componentPlayer == null ||
                camera.GameWidget.PlayerData == m_componentPlayer.PlayerData)
                return;
            Vector3 position = Vector3.Transform(
                m_componentCreature.ComponentBody.Position + 1.02f * Vector3.UnitY *
                    m_componentCreature.ComponentBody.BoxSize.Y,
                camera.ViewMatrix);
            if (position.Z >= 0f) return;
            Color color = Color.Lerp(Color.White, Color.Transparent,
                MathUtils.Saturate((position.Length() - 4f) / 3f));
            if (color.A <= 8) return;
            BitmapFont font = MultiplayerChineseFont.Font ??
                ContentManager.Get<BitmapFont>("Fonts/Pericles32");
            Vector3 right = Vector3.TransformNormal(0.0065f * Vector3.Normalize(
                Vector3.Cross(camera.ViewDirection, Vector3.UnitY)), camera.ViewMatrix);
            Vector3 down = Vector3.TransformNormal(-0.0065f * Vector3.UnitY,
                camera.ViewMatrix);
            m_subsystemModelsRenderer.PrimitivesRenderer.FontBatch(font, 1,
                DepthStencilState.DepthRead, RasterizerState.CullNoneScissor,
                BlendState.AlphaBlend, SamplerState.LinearClamp).QueueText(
                    m_componentPlayer.PlayerData.Name, position, right, down, color,
                    TextAnchor.HorizontalCenter | TextAnchor.Bottom);
        }
    }
}
