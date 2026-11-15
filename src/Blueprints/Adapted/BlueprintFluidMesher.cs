// Adapted from Buildwright 9b15446; covered by the project MIT LICENSE.
// CPU liquid geometry only; never registers or modifies world render pools.
using System.Reflection;
using VSDirector.Blueprints;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace VSDirector.Blueprints;

/// <summary>
/// Generates liquid geometry in an isolated chunk tesselator. The resulting
/// meshes are owned by Director; no world render pools are modified.
/// </summary>
internal sealed class BlueprintFluidMesher
{
    private const int ChunkSize = 32;
    private const int ExtChunkSize = 34;
    private const int ExtLayerSize = ExtChunkSize * ExtChunkSize;
    private const int ExtVolume = ExtChunkSize * ExtChunkSize * ExtChunkSize;
    private const int FullBright = -1;
    private static readonly BindingFlags InstanceFields = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private static readonly object TesselatorLock = new();
    private static ChunkTesselator? sharedTesselator;
    private static ClientMain? sharedGame;
    internal static void Release(){lock(TesselatorLock){sharedTesselator=null;sharedGame=null;}}

    private static readonly FieldInfo? CoreGame = typeof(ClientCoreAPI).GetField("game", InstanceFields);
    private static readonly FieldInfo? MainChunkRenderer = typeof(ClientMain).GetField("chunkRenderer", InstanceFields);
    private static readonly FieldInfo? BlocksExt = typeof(ChunkTesselator).GetField("currentChunkBlocksExt", InstanceFields);
    private static readonly FieldInfo? FluidBlocksExt = typeof(ChunkTesselator).GetField("currentChunkFluidBlocksExt", InstanceFields);
    private static readonly FieldInfo? LightExt = typeof(ChunkTesselator).GetField("currentChunkRgbsExt", InstanceFields);
    private static readonly FieldInfo? DrawSolid = typeof(ChunkTesselator).GetField("currentChunkDraw32", InstanceFields);
    private static readonly FieldInfo? DrawFluid = typeof(ChunkTesselator).GetField("currentChunkDrawFluids", InstanceFields);
    private static readonly FieldInfo? ClimateMap = typeof(ChunkTesselator).GetField("currentClimateRegionMap", InstanceFields);
    private static readonly FieldInfo? RegionSize = typeof(ChunkTesselator).GetField("regionSize", InstanceFields);
    private static readonly FieldInfo? Vars = typeof(ChunkTesselator).GetField("vars", InstanceFields);
    private static readonly FieldInfo? CenterPools = typeof(ChunkTesselator).GetField("centerModeldataByRenderPassByLodLevel", InstanceFields);
    private static readonly FieldInfo? EdgePools = typeof(ChunkTesselator).GetField("edgeModeldataByRenderPassByLodLevel", InstanceFields);
    private static readonly MethodInfo? SetDimension = typeof(TCTCache).GetMethod("SetDimension", InstanceFields);

    private readonly ICoreClientAPI api;
    private readonly IReadOnlyDictionary<(int X,int Y,int Z),Cell> cells;
    private readonly Queue<Int3> chunks;
    internal bool Ready => chunks.Count == 0;
    internal BlueprintFluidMesher(ICoreClientAPI api,BlockSchematic schematic)
    {
        this.api=api;cells=ReadCells(api,schematic,null);
        chunks=new(cells.Where(p=>p.Value.Fluid is not null || p.Value.Solid?.DrawType==EnumDrawType.Liquid)
            .Select(p=>(p.Key.X/32,p.Key.Y/32,p.Key.Z/32)).Distinct().Select(p=>new Int3(p.Item1,p.Item2,p.Item3)));
    }
    internal IEnumerable<BlueprintMeshPart> Next()
    {
        if(chunks.Count==0)return [];
        var origin=api.World.Player.Entity.Pos.AsBlockPos;
        List<TerrainPart> parts;
        lock(TesselatorLock)parts=BuildParts(api,GetTesselator((ClientMain)api.World),cells,origin,chunks.Dequeue());
        var output=new List<BlueprintMeshPart>();
        foreach(var part in parts)
        {
            if(part.Lod!=0){part.Mesh.Dispose();continue;}
            var mesh=part.Mesh;mesh.Translate(part.ChunkOffset.X,part.ChunkOffset.Y,part.ChunkOffset.Z);
            mesh.TextureIds=[api.BlockTextureAtlas.AtlasTextures[part.Atlas].TextureId];
            mesh.TextureIndicesCount=(mesh.IndicesCount+mesh.IndicesPerFace-1)/mesh.IndicesPerFace;
            mesh.TextureIndices=new byte[mesh.TextureIndicesCount];
            mesh.CustomInts=null;mesh.CustomFloats=null;mesh.CustomBytes=null;mesh.CustomShorts=null;
            output.Add(new(mesh,true));
        }
        return output;
    }
    private static ChunkTesselator GetTesselator(ClientMain game)
    {
        if (sharedTesselator != null && ReferenceEquals(sharedGame, game)) return sharedTesselator;
        var tesselator = new ChunkTesselator(game);
        tesselator.LightlevelsReceived();
        tesselator.BlockTexturesLoaded();
        sharedGame = game;
        return sharedTesselator = tesselator;
    }

    private static List<TerrainPart> BuildParts(ICoreClientAPI capi, ChunkTesselator tesselator,
        IReadOnlyDictionary<(int X, int Y, int Z), Cell> cells, BlockPos environmentOrigin, Int3 chunk)
    {
        EnsureReflectionTargets();
        var air = capi.World.GetBlock(0);

        var blocks = (Block[])BlocksExt!.GetValue(tesselator)!;
        var fluids = (Block[])FluidBlocksExt!.GetValue(tesselator)!;
        var lights = (int[])LightExt!.GetValue(tesselator)!;
        var drawSolid = (byte[])DrawSolid!.GetValue(tesselator)!;
        var drawFluid = (byte[])DrawFluid!.GetValue(tesselator)!;
        var vars = (TCTCache)Vars!.GetValue(tesselator)!;
        vars.textureAtlasPositionsByTextureSubId = capi.BlockTextureAtlas.Positions;
        vars.aoAndSmoothShadows = false;
        vars.rainHeightMap = new ushort[ChunkSize * ChunkSize];
        var regionSize = (int)RegionSize!.GetValue(tesselator)!;
        ClimateMap!.SetValue(tesselator, BuildClimateMap(capi, environmentOrigin, regionSize));

        var parts = new List<TerrainPart>();
        int cx=chunk.X,cy=chunk.Y,cz=chunk.Z;
        for(int once=0;once<1;once++)
        {
            Array.Fill(blocks, air);
            Array.Fill(fluids, air);
            Array.Fill(lights, FullBright);
            Array.Clear(drawSolid);
            Array.Clear(drawFluid);
            FillExtendedArrays(cells, blocks, fluids, cx * ChunkSize, cy * ChunkSize, cz * ChunkSize, air);

            var baseX = FloorToChunk(environmentOrigin.X + cx * ChunkSize);
            var baseY = FloorToChunk(environmentOrigin.Y + cy * ChunkSize);
            var baseZ = FloorToChunk(environmentOrigin.Z + cz * ChunkSize);
            SetDimension!.Invoke(vars, new object[] { environmentOrigin.dimension });
            vars.xMin = vars.yMin = vars.zMin = ChunkSize;
            vars.xMax = vars.yMax = vars.zMax = 0;
            var any = tesselator.CalculateVisibleFaces_Fluids(false, baseX, baseY, baseZ)
                | tesselator.CalculateVisibleFaces(false, baseX, baseY, baseZ);
            if (!any) continue;
            SuppressStandardBlocks(blocks, fluids, drawSolid, drawFluid);
            if (!drawSolid.Any(value => value != 0) && !drawFluid.Any(value => value != 0)) continue;

            ClearPools(tesselator);
            tesselator.BuildBlockPolygons(baseX / ChunkSize,
                baseY / ChunkSize + environmentOrigin.dimension * 1024, baseZ / ChunkSize);
            CapturePools(tesselator, new Int3(cx * ChunkSize, cy * ChunkSize, cz * ChunkSize), parts);
        }
        return parts;
    }

    private static Dictionary<(int X, int Y, int Z), Cell> ReadCells(ICoreClientAPI capi,
        BlockSchematic schematic, System.Func<int, int, int, bool>? includeCell)
    {
        var cells = new Dictionary<(int X, int Y, int Z), Cell>();
        for (var index = 0; index < schematic.Indices.Count && index < schematic.BlockIds.Count; index++)
        {
            var packed = schematic.Indices[index];
            var x = (int)(packed & 0x3ff);
            var z = (int)((packed >> 10) & 0x3ff);
            var y = (int)((packed >> 20) & 0x3ff);
            if (includeCell != null && !includeCell(x, y, z)) continue;
            if (!schematic.BlockCodes.TryGetValue(schematic.BlockIds[index], out var code)) continue;
            var block = capi.World.GetBlock(code);
            if (block == null || block.Id == 0) continue;
            cells.TryGetValue((x, y, z), out var cell);
            if (block.ForFluidsLayer) cell.Fluid = block;
            else cell.Solid = block;
            cells[(x, y, z)] = cell;
        }
        return cells;
    }

    private static void FillExtendedArrays(IReadOnlyDictionary<(int X, int Y, int Z), Cell> cells,
        Block[] blocks, Block[] fluids, int offsetX, int offsetY, int offsetZ, Block air)
    {
        for (var y = 0; y < ExtChunkSize; y++)
        for (var z = 0; z < ExtChunkSize; z++)
        for (var x = 0; x < ExtChunkSize; x++)
        {
            var index = y * ExtLayerSize + z * ExtChunkSize + x;
            if (!cells.TryGetValue((offsetX + x - 1, offsetY + y - 1, offsetZ + z - 1), out var cell))
                continue;
            blocks[index] = cell.Solid ?? air;
            fluids[index] = cell.Fluid ?? air;
        }
    }

    private static void SuppressStandardBlocks(Block[] blocks, Block[] fluids, byte[] solidDraw, byte[] fluidDraw)
    {
        var index3d = 0;
        for (var y = 0; y < ChunkSize; y++)
        for (var z = 0; z < ChunkSize; z++)
        for (var x = 0; x < ChunkSize; x++, index3d++)
        {
            var ext = (y + 1) * ExtLayerSize + (z + 1) * ExtChunkSize + x + 1;
            if (!RequiresTerrainPipeline(blocks[ext])) solidDraw[index3d] = 0;
            if (!RequiresTerrainPipeline(fluids[ext])) fluidDraw[index3d] = 0;
        }
    }

    private static bool RequiresTerrainPipeline(Block block) =>
        block.Id != 0 && (block.ForFluidsLayer || block.DrawType == EnumDrawType.Liquid);

    private static int[] BuildClimateMap(ICoreClientAPI capi, BlockPos origin, int regionSize)
    {
        var map = new int[Math.Max(1, regionSize * regionSize)];
        var temperature = 128;
        var rainfall = 128;
        try
        {
            var climate = capi.World.BlockAccessor.GetClimateAt(origin, EnumGetClimateMode.WorldGenValues, 0);
            temperature = Math.Clamp((int)MathF.Round((climate.Temperature + 20f) * Climate.TemperatureScaleConversion), 0, 255);
            rainfall = Math.Clamp((int)MathF.Round(climate.Rainfall * 255f), 0, 255);
        }
        catch { }
        Array.Fill(map, temperature << 16 | rainfall << 8);
        return map;
    }

    private static int FloorToChunk(int coordinate)
    {
        var quotient = coordinate / ChunkSize;
        if (coordinate < 0 && coordinate % ChunkSize != 0) quotient--;
        return quotient * ChunkSize;
    }

    private static void ClearPools(ChunkTesselator tesselator)
    {
        Clear((MeshData[][][])CenterPools!.GetValue(tesselator)!);
        Clear((MeshData[][][])EdgePools!.GetValue(tesselator)!);
        static void Clear(MeshData[][][] pools)
        {
            foreach (var lod in pools)
            foreach (var pass in lod)
            foreach (var mesh in pass) mesh.Clear();
        }
    }

    private static void CapturePools(ChunkTesselator tesselator, Int3 chunkOffset, ICollection<TerrainPart> output)
    {
        Capture((MeshData[][][])CenterPools!.GetValue(tesselator)!);
        Capture((MeshData[][][])EdgePools!.GetValue(tesselator)!);
        void Capture(MeshData[][][] pools)
        {
            for (var lod = 0; lod < pools.Length; lod++)
            for (var pass = 0; pass < pools[lod].Length; pass++)
            for (var atlas = 0; atlas < pools[lod][pass].Length; atlas++)
            {
                var source = pools[lod][pass][atlas];
                if (source.VerticesCount == 0) continue;
                var mesh = source.Clone();
                mesh.CompactBuffers();
                NormalizeInterleave(mesh, (EnumChunkRenderPass)pass);
                output.Add(new TerrainPart(chunkOffset.Copy(), (EnumChunkRenderPass)pass, atlas, lod, mesh));
            }
        }
    }

    private static void NormalizeInterleave(MeshData mesh, EnumChunkRenderPass pass)
    {
        if (mesh.CustomInts != null)
        {
            mesh.CustomInts.Conversion = DataConversion.Integer;
            mesh.CustomInts.InterleaveSizes = pass == EnumChunkRenderPass.Liquid ? new[] { 1, 1 } : new[] { 1 };
            mesh.CustomInts.InterleaveOffsets = pass == EnumChunkRenderPass.Liquid ? new[] { 0, 4 } : new[] { 0 };
            mesh.CustomInts.InterleaveStride = pass == EnumChunkRenderPass.Liquid ? 8 : 4;
        }
        if (mesh.CustomFloats != null && pass == EnumChunkRenderPass.Liquid)
        {
            mesh.CustomFloats.InterleaveSizes = new[] { 2 };
            mesh.CustomFloats.InterleaveOffsets = new[] { 0 };
            mesh.CustomFloats.InterleaveStride = 8;
        }
        if (mesh.CustomShorts != null && pass == EnumChunkRenderPass.TopSoil)
        {
            mesh.CustomShorts.Conversion = DataConversion.NormalizedFloat;
            mesh.CustomShorts.InterleaveSizes = new[] { 2 };
            mesh.CustomShorts.InterleaveOffsets = new[] { 0 };
            mesh.CustomShorts.InterleaveStride = 4;
        }
    }

    private static void EnsureReflectionTargets()
    {
        if (CoreGame == null || MainChunkRenderer == null || BlocksExt == null || FluidBlocksExt == null
            || LightExt == null || DrawSolid == null || DrawFluid == null || ClimateMap == null
            || RegionSize == null || Vars == null || CenterPools == null || EdgePools == null || SetDimension == null)
            throw new MissingMemberException("Vintage Story chunk tesselator fields are unavailable.");
    }

    private struct Cell
    {
        public Block? Solid;
        public Block? Fluid;
    }

    private sealed record TerrainPart(Int3 ChunkOffset, EnumChunkRenderPass Pass, int Atlas, int Lod, MeshData Mesh);
}
