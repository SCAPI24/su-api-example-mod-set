using Game;

namespace SuAPIShadows.Foliage;

// Source: TallGrassBlock.cs:TallGrassBlock.GenerateTerrainVertices (inherited native generator where applicable).
public sealed class FoliageTallGrassBlock : TallGrassBlock
{
    public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry, int value, int x, int y, int z)
    {
        var ranges = new FoliageVertexEncoding.Ranges(geometry);
        base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
        ranges.Mark(FoliageKind.Grass, y, 1f, TallGrassBlock.GetIsSmall(Terrain.ExtractData(value)) ? 0.35f : 1f);
    }
}

// Source: RedFlowerBlock.cs:RedFlowerBlock.GenerateTerrainVertices (inherited native generator where applicable).
public sealed class FoliageRedFlowerBlock : RedFlowerBlock
{
    public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry, int value, int x, int y, int z)
    {
        var ranges = new FoliageVertexEncoding.Ranges(geometry);
        base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
        ranges.Mark(FoliageKind.SoftPlant, y, 1f, FlowerBlock.GetIsSmall(Terrain.ExtractData(value)) ? 0.35f : 1f);
    }
}

// Source: WhiteFlowerBlock.cs:WhiteFlowerBlock.GenerateTerrainVertices (inherited native generator where applicable).
public sealed class FoliageWhiteFlowerBlock : WhiteFlowerBlock
{
    public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry, int value, int x, int y, int z)
    {
        var ranges = new FoliageVertexEncoding.Ranges(geometry);
        base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
        ranges.Mark(FoliageKind.SoftPlant, y, 1f, FlowerBlock.GetIsSmall(Terrain.ExtractData(value)) ? 0.35f : 1f);
    }
}

// Source: PurpleFlowerBlock.cs:PurpleFlowerBlock.GenerateTerrainVertices (inherited native generator where applicable).
public sealed class FoliagePurpleFlowerBlock : PurpleFlowerBlock
{
    public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry, int value, int x, int y, int z)
    {
        var ranges = new FoliageVertexEncoding.Ranges(geometry);
        base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
        ranges.Mark(FoliageKind.SoftPlant, y, 1f, FlowerBlock.GetIsSmall(Terrain.ExtractData(value)) ? 0.35f : 1f);
    }
}

// Source: RyeBlock.cs:RyeBlock.GenerateTerrainVertices (inherited native generator where applicable).
public sealed class FoliageRyeBlock : RyeBlock
{
    public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry, int value, int x, int y, int z)
    {
        var ranges = new FoliageVertexEncoding.Ranges(geometry);
        base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
        ranges.Mark(FoliageKind.SoftPlant, y, 1f, (RyeBlock.GetSize(Terrain.ExtractData(value)) + 1) / 8f);
    }
}

// Source: CottonBlock.cs:CottonBlock.GenerateTerrainVertices (inherited native generator where applicable).
public sealed class FoliageCottonBlock : CottonBlock
{
    public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry, int value, int x, int y, int z)
    {
        var ranges = new FoliageVertexEncoding.Ranges(geometry);
        base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
        ranges.Mark(FoliageKind.SoftPlant, y, 1f, (CottonBlock.GetSize(Terrain.ExtractData(value)) + 1) / 3f);
    }
}

// Source: PumpkinBlock.cs:PumpkinBlock.GenerateTerrainVertices (inherited native generator where applicable).
public sealed class FoliagePumpkinBlock : PumpkinBlock
{
    public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry, int value, int x, int y, int z)
    {
        var ranges = new FoliageVertexEncoding.Ranges(geometry);
        base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
        ranges.Mark(FoliageKind.SoftPlant, y, 1f, BasePumpkinBlock.GetSize(Terrain.ExtractData(value)) == 0 ? 0.35f : 1f);
    }
}

// Source: RottenPumpkinBlock.cs:RottenPumpkinBlock.GenerateTerrainVertices (inherited native generator where applicable).
public sealed class FoliageRottenPumpkinBlock : RottenPumpkinBlock
{
    public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry, int value, int x, int y, int z)
    {
        var ranges = new FoliageVertexEncoding.Ranges(geometry);
        base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
        ranges.Mark(FoliageKind.SoftPlant, y, 1f, BasePumpkinBlock.GetSize(Terrain.ExtractData(value)) == 0 ? 0.35f : 1f);
    }
}

// Source: DryBushBlock.cs:DryBushBlock.GenerateTerrainVertices (inherited native generator where applicable).
public sealed class FoliageDryBushBlock : DryBushBlock
{
    public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry, int value, int x, int y, int z)
    {
        var ranges = new FoliageVertexEncoding.Ranges(geometry);
        base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
        ranges.Mark(FoliageKind.Bush, y, 1f, 1f);
    }
}

// Source: LargeDryBushBlock.cs:LargeDryBushBlock.GenerateTerrainVertices (inherited native generator where applicable).
public sealed class FoliageLargeDryBushBlock : LargeDryBushBlock
{
    public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry, int value, int x, int y, int z)
    {
        var ranges = new FoliageVertexEncoding.Ranges(geometry);
        base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
        ranges.Mark(FoliageKind.Bush, y, 1f, 1f);
    }
}

// Source: KelpBlock.cs:KelpBlock.GenerateTerrainVertices (inherited native generator where applicable).
public sealed class FoliageKelpBlock : KelpBlock
{
    public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry, int value, int x, int y, int z)
    {
        var ranges = new FoliageVertexEncoding.Ranges(geometry);
        base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
        ranges.Mark(FoliageKind.Water, FoliageVertexEncoding.FindKelpRoot(generator.Terrain, x, y, z), 8f, 1f);
    }
}

// Source: SeagrassBlock.cs:SeagrassBlock.GenerateTerrainVertices (inherited native generator where applicable).
public sealed class FoliageSeagrassBlock : SeagrassBlock
{
    public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry, int value, int x, int y, int z)
    {
        var ranges = new FoliageVertexEncoding.Ranges(geometry);
        base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
        ranges.Mark(FoliageKind.Water, y, 1f, 1f);
    }
}

// Source: BirchLeavesBlock.cs:BirchLeavesBlock.GenerateTerrainVertices (inherited native generator where applicable).
public sealed class FoliageBirchLeavesBlock : BirchLeavesBlock
{
    public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry, int value, int x, int y, int z)
    {
        var ranges = new FoliageVertexEncoding.Ranges(geometry);
        base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
        ranges.Mark(FoliageKind.Leaves, y, 1f, 1f);
    }
}

// Source: MimosaLeavesBlock.cs:MimosaLeavesBlock.GenerateTerrainVertices (inherited native generator where applicable).
public sealed class FoliageMimosaLeavesBlock : MimosaLeavesBlock
{
    public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry, int value, int x, int y, int z)
    {
        var ranges = new FoliageVertexEncoding.Ranges(geometry);
        base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
        ranges.Mark(FoliageKind.Leaves, y, 1f, 1f);
    }
}

// Source: OakLeavesBlock.cs:OakLeavesBlock.GenerateTerrainVertices (inherited native generator where applicable).
public sealed class FoliageOakLeavesBlock : OakLeavesBlock
{
    public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry, int value, int x, int y, int z)
    {
        var ranges = new FoliageVertexEncoding.Ranges(geometry);
        base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
        ranges.Mark(FoliageKind.Leaves, y, 1f, 1f);
    }
}

// Source: PoplarLeavesBlock.cs:PoplarLeavesBlock.GenerateTerrainVertices (inherited native generator where applicable).
public sealed class FoliagePoplarLeavesBlock : PoplarLeavesBlock
{
    public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry, int value, int x, int y, int z)
    {
        var ranges = new FoliageVertexEncoding.Ranges(geometry);
        base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
        ranges.Mark(FoliageKind.Leaves, y, 1f, 1f);
    }
}

// Source: SpruceLeavesBlock.cs:SpruceLeavesBlock.GenerateTerrainVertices (inherited native generator where applicable).
public sealed class FoliageSpruceLeavesBlock : SpruceLeavesBlock
{
    public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry, int value, int x, int y, int z)
    {
        var ranges = new FoliageVertexEncoding.Ranges(geometry);
        base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
        ranges.Mark(FoliageKind.Leaves, y, 1f, 1f);
    }
}

// Source: TallSpruceLeavesBlock.cs:TallSpruceLeavesBlock.GenerateTerrainVertices (inherited native generator where applicable).
public sealed class FoliageTallSpruceLeavesBlock : TallSpruceLeavesBlock
{
    public override void GenerateTerrainVertices(BlockGeometryGenerator generator, TerrainGeometry geometry, int value, int x, int y, int z)
    {
        var ranges = new FoliageVertexEncoding.Ranges(geometry);
        base.GenerateTerrainVertices(generator, geometry, value, x, y, z);
        ranges.Mark(FoliageKind.Leaves, y, 1f, 1f);
    }
}
