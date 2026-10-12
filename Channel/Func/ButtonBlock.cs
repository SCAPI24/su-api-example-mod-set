using System;
using System.Collections.Generic;
using Engine;
using Engine.Graphics;
using Game;
using SuEventBus;

namespace Channel
{
    /// <summary>
    /// 信道方块：复用 button 的 index（142），用 data 的 14-17 位区分元件类型。
    ///
    ///   bit 0-2   : 安装面 face 0..5（与 button 完全一致，vanilla 也读，必须 0..5）
    ///   bit 3-10  : 通道号 0..255（低 4 位 3-6、高 4 位 7-10；默认 0 号信道）
    ///   bit 11-12 : 朝向 0..3
    ///   bit 13    : 空（恒 0，不参与判断）
    ///   bit 14-17 : 类型码 0=button、1=信道、2..15 预留
    ///
    /// 类名必须保持 "ButtonBlock"：Pak/CraftingRecipes.xml 的 Result="ButtonBlock"
    /// 与 ComponentLoot 都按 GetType().Name 解析（CraftingRecipesManager.DecodeResult）。
    /// </summary>
    public sealed class ButtonBlock : Game.ButtonBlock
    {
        public const int TypeShift = 14;
        public const int TypeMask = 0xF;
        public const int ChannelType = 1;
        public const int ChannelNumberMask = 0x7F8;   // bit 3..10
        public const int RotationMask = 0x1800;       // bit 11..12

        private BlockMesh[] m_channelMeshes = new BlockMesh[24];
        private BoundingBox[][] m_channelCollisionBoxes = new BoundingBox[24][];
        private BlockMesh m_channelStandaloneMesh = new BlockMesh();

        // ───────────────────────── data 访问器 ─────────────────────────

        public static int GetElementType(int data)
        {
            return (data >> TypeShift) & TypeMask;
        }

        public static bool IsChannel(int data)
        {
            return GetElementType(data) == ChannelType;
        }

        public static int GetChannelNumber(int data)
        {
            return (data >> 3) & 0xFF;
        }

        public static int SetChannelNumber(int data, int channel)
        {
            return (data & ~ChannelNumberMask) | ((channel & 0xFF) << 3);
        }

        public static int GetRotation(int data)
        {
            return (data >> 11) & 3;
        }

        public static int SetRotation(int data, int rotation)
        {
            return (data & ~RotationMask) | ((rotation & 3) << 11);
        }

        public static int MakeChannelData(int face, int rotation, int channel)
        {
            return (face & 7)
                | ((channel & 0xFF) << 3)
                | ((rotation & 3) << 11)
                | (ChannelType << TypeShift);
        }

        /// <summary>创造栏/掉落用的信道本体：通道号归零（保证可堆叠）。</summary>
        public static int MakeChannelValue()
        {
            return Terrain.MakeBlockValue(Game.ButtonBlock.Index, 0, ChannelType << TypeShift);
        }

        // ───────────────────────── 资源 ─────────────────────────

        public override void Initialize()
        {
            // 按钮贴图（按钮格用）
            base.Initialize();

            // 信道贴图：SR latch 模型，24 套 (face, rotation)
            // Source: Survivalcraft/Game/RotateableMountedElectricElementBlock.cs:Initialize
            Model model = ContentManager.Get<Model>("Models/Gates");
            Matrix boneAbsoluteTransform = BlockMesh.GetBoneAbsoluteTransform(model.FindMesh("SRLatch").ParentBone);
            for (int face = 0; face < 6; face++)
            {
                float radians;
                bool flag;
                if (face < 4)
                {
                    radians = (float)face * (float)Math.PI / 2f;
                    flag = false;
                }
                else if (face == 4)
                {
                    radians = -(float)Math.PI / 2f;
                    flag = true;
                }
                else
                {
                    radians = (float)Math.PI / 2f;
                    flag = true;
                }
                for (int rotation = 0; rotation < 4; rotation++)
                {
                    float rotationRadians = (float)(-rotation) * (float)Math.PI / 2f;
                    int index = (face << 2) + rotation;
                    Matrix matrix = Matrix.CreateRotationX((float)Math.PI / 2f)
                        * Matrix.CreateRotationZ(rotationRadians)
                        * Matrix.CreateTranslation(0f, 0f, -0.5f)
                        * (flag ? Matrix.CreateRotationX(radians) : Matrix.CreateRotationY(radians))
                        * Matrix.CreateTranslation(0.5f, 0.5f, 0.5f);
                    m_channelMeshes[index] = new BlockMesh();
                    m_channelMeshes[index].AppendModelMeshPart(model.FindMesh("SRLatch").MeshParts[0],
                        boneAbsoluteTransform * matrix, makeEmissive: false, flipWindingOrder: false,
                        doubleSided: false, flipNormals: false, Color.White);
                    m_channelCollisionBoxes[index] = new BoundingBox[1] { m_channelMeshes[index].CalculateBoundingBox() };
                }
            }

            Matrix standaloneTransform = Matrix.CreateRotationY(-(float)Math.PI / 2f)
                * Matrix.CreateRotationZ((float)Math.PI / 2f);
            m_channelStandaloneMesh.AppendModelMeshPart(model.FindMesh("SRLatch").MeshParts[0],
                boneAbsoluteTransform * standaloneTransform, makeEmissive: false, flipWindingOrder: false,
                doubleSided: false, flipNormals: false, Color.White);
        }

        /// <summary>face(bit0-2) 与朝向(bit11-12) → 预烘网格/碰撞箱下标 0..23。</summary>
        public static int GetMeshIndex(int data)
        {
            return ((data & 7) << 2) | ((data >> 11) & 3);
        }

        // ───────────────────────── 渲染 ─────────────────────────

        public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry,
            int value, int x, int y, int z)
        {
            if (!IsChannel(Terrain.ExtractData(value)))
            {
                base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
                return;
            }

            int data = Terrain.ExtractData(value);
            int face = data & 7;
            generator.GenerateMeshVertices(this, x, y, z, m_channelMeshes[GetMeshIndex(Terrain.ExtractData(value))],
                Color.White, null, geometry.SubsetOpaque);
            generator.GenerateWireVertices(value, x, y, z, face, 0.375f, Vector2.Zero, geometry.SubsetOpaque);
        }

        public override void DrawBlock(PrimitivesRenderer3D primitivesRenderer, int value, Color color, float size,
            ref Matrix matrix, DrawBlockEnvironmentData environmentData)
        {
            if (!IsChannel(Terrain.ExtractData(value)))
            {
                base.DrawBlock(primitivesRenderer, value, color, size, ref matrix, environmentData);
                return;
            }
            BlocksManager.DrawMeshBlock(primitivesRenderer, m_channelStandaloneMesh, color, 2f * size,
                ref matrix, environmentData);
        }

        public override BoundingBox[] GetCustomCollisionBoxes(SubsystemTerrain terrain, int value)
        {
            if (!IsChannel(Terrain.ExtractData(value)))
                return base.GetCustomCollisionBoxes(terrain, value);
            return m_channelCollisionBoxes[GetMeshIndex(Terrain.ExtractData(value))];
        }

        // ───────────────────────── 电学 ─────────────────────────

        public override ElectricElement CreateElectricElement(SubsystemElectricity subsystemElectricity,
            int value, int x, int y, int z)
        {
            if (IsChannel(Terrain.ExtractData(value)))
                return new ChannelElectricElement(subsystemElectricity, new CellFace(x, y, z, GetFace(value)));

            // 按钮格：先问事件总线（联机 Mod 网络化按钮元件），没人接管才回原版
            CircuitElementRequest request = new CircuitElementRequest
            {
                SubsystemElectricity = subsystemElectricity,
                Value = value,
                X = x,
                Y = y,
                Z = z
            };
            SuEventHub.Publish(request);
            if (request.Result != null)
                return request.Result;

            return base.CreateElectricElement(subsystemElectricity, value, x, y, z);
        }

        public override ElectricConnectorType? GetConnectorType(SubsystemTerrain terrain, int value, int face,
            int connectorFace, int x, int y, int z)
        {
            if (!IsChannel(Terrain.ExtractData(value)))
                return base.GetConnectorType(terrain, value, face, connectorFace, x, y, z);

            int mountingFace = GetFace(value);
            if (mountingFace != face)
                return null;

            // Source: Survivalcraft/Game/SRLatchBlock.cs:GetConnectorType
            ElectricConnectorDirection? direction = SubsystemElectricity.GetConnectorDirection(
                mountingFace, GetRotation(Terrain.ExtractData(value)), connectorFace);
            if (direction == ElectricConnectorDirection.Left
                || direction == ElectricConnectorDirection.Right
                || direction == ElectricConnectorDirection.Bottom)
            {
                return ElectricConnectorType.Input;
            }
            if (direction == ElectricConnectorDirection.Top || direction == ElectricConnectorDirection.In)
            {
                // Top 与 In（穿墙/背面）= 同一根线，双向
                return ElectricConnectorType.InputOutput;
            }
            return null;
        }

        // ───────────────────────── 放置 / 掉落 ─────────────────────────

        public override BlockPlacementData GetPlacementValue(SubsystemTerrain subsystemTerrain,
            ComponentMiner componentMiner, int value, TerrainRaycastResult raycastResult)
        {
            int data = Terrain.ExtractData(value);
            if (!IsChannel(data))
                return base.GetPlacementValue(subsystemTerrain, componentMiner, value, raycastResult);

            int rotation = 0;
            if (raycastResult.CellFace.Face >= 4)
            {
                // Source: Survivalcraft/Game/RotateableMountedElectricElementBlock.cs:GetPlacementValue
                Vector3 forward = Matrix.CreateFromQuaternion(
                    componentMiner.ComponentCreature.ComponentCreatureModel.EyeRotation).Forward;
                float z = Vector3.Dot(forward, Vector3.UnitZ);
                float x = Vector3.Dot(forward, Vector3.UnitX);
                float nz = Vector3.Dot(forward, -Vector3.UnitZ);
                float nx = Vector3.Dot(forward, -Vector3.UnitX);
                if (z == MathUtils.Max(z, x, nz, nx))
                    rotation = 2;
                else if (x == MathUtils.Max(z, x, nz, nx))
                    rotation = 1;
                else if (nz == MathUtils.Max(z, x, nz, nx))
                    rotation = 0;
                else if (nx == MathUtils.Max(z, x, nz, nx))
                    rotation = 3;
            }

            // 保留类型码与通道号（手持编辑过的通道号必须跟着走），只重写 face 与朝向
            BlockPlacementData result = default(BlockPlacementData);
            result.Value = Terrain.MakeBlockValue(BlockIndex, 0,
                MakeChannelData(raycastResult.CellFace.Face, rotation, GetChannelNumber(data)));
            result.CellFace = raycastResult.CellFace;
            return result;
        }

        public override void GetDropValues(SubsystemTerrain subsystemTerrain, int oldValue, int newValue,
            int toolLevel, List<BlockDropValue> dropValues, out bool showDebris)
        {
            if (!IsChannel(Terrain.ExtractData(oldValue)))
            {
                base.GetDropValues(subsystemTerrain, oldValue, newValue, toolLevel, dropValues, out showDebris);
                return;
            }

            // 像按钮那样掉本体；通道号归零 → 所有信道物品同一个值 → 可堆叠
            showDebris = DestructionDebrisScale > 0f;
            if (toolLevel < RequiredToolLevel)
                return;
            dropValues.Add(new BlockDropValue
            {
                Value = MakeChannelValue(),
                Count = 1
            });
        }

        // ───────────────────────── 创造栏 / 文案 / 配方 ─────────────────────────

        public override IEnumerable<int> GetCreativeValues()
        {
            foreach (int value in base.GetCreativeValues())
                yield return value;
            yield return MakeChannelValue();
        }

        public override string GetDisplayName(SubsystemTerrain subsystemTerrain, int value)
        {
            if (IsChannel(Terrain.ExtractData(value)))
                return ChannelText.Channel;
            return base.GetDisplayName(subsystemTerrain, value);
        }

        public override string GetDescription(int value)
        {
            if (IsChannel(Terrain.ExtractData(value)))
                return ChannelText.ChannelDescription;
            return base.GetDescription(value);
        }

        public override IEnumerable<CraftingRecipe> GetProceduralCraftingRecipes()
        {
            foreach (CraftingRecipe recipe in base.GetProceduralCraftingRecipes())
                yield return recipe;

            // 图案（与 SR latch 同材料，形状为其上下翻转，已实测不与任何原版配方冲突）：
            //   bbb
            //   bab
            //     a
            CraftingRecipe channelRecipe = new CraftingRecipe
            {
                ResultCount = 4,
                ResultValue = MakeChannelValue(),
                RequiredHeatLevel = 0f,
                RequiredPlayerLevel = 1f,
                Description = ChannelText.RecipeDescription
            };
            channelRecipe.Ingredients[0] = "germaniumchunk";
            channelRecipe.Ingredients[1] = "germaniumchunk";
            channelRecipe.Ingredients[2] = "germaniumchunk";
            channelRecipe.Ingredients[3] = "germaniumchunk";
            channelRecipe.Ingredients[4] = "copperingot";
            channelRecipe.Ingredients[5] = "germaniumchunk";
            channelRecipe.Ingredients[6] = null;
            channelRecipe.Ingredients[7] = null;
            channelRecipe.Ingredients[8] = "copperingot";
            yield return channelRecipe;
        }
    }
}
