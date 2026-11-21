using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using VSDirector.Data;

namespace VSDirector.Replay;

/// <summary>Reads the identity and the block/item numbering of the world the client is in.</summary>
internal static class DirectorReplayWorldIdentity
{
    public static DirectorReplayWorldInfo CaptureInfo(ICoreClientAPI capi, string label)
    {
        IWorldAccessor world = capi.World;
        DirectorReplayWorldInfo info = new()
        {
            WorldId = world.SavegameIdentifier ?? string.Empty,
            Label = label ?? string.Empty,
            Singleplayer = capi.IsSinglePlayer,
            GameVersion = Vintagestory.API.Config.GameVersion.ShortGameVersion,
            Seed = world.Seed,
            MapSizeX = world.BlockAccessor.MapSizeX,
            MapSizeY = world.BlockAccessor.MapSizeY,
            MapSizeZ = world.BlockAccessor.MapSizeZ,
            SeaLevel = world.SeaLevel,
            CapturedUtc = DateTimeOffset.UtcNow
        };
        try
        {
            info.PlayStyle = world.Config?.GetString("playstyle") ?? string.Empty;
            info.WorldType = world.Config?.GetString("worldType") ?? string.Empty;
        }
        catch (Exception)
        {
            // The world config is informational only.
        }

        try
        {
            info.Mods = capi.ModLoader.Mods
                .Where(mod => mod?.Info is not null && !string.IsNullOrWhiteSpace(mod.Info.ModID))
                .Select(mod => mod.Info.ModID + "@" + mod.Info.Version)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            info.Mods = new List<string>();
        }

        return info;
    }

    public static DirectorReplayIdTable CaptureIds(IWorldAccessor world)
    {
        DirectorReplayIdTable table = new();
        IList<Block> blocks = world.Blocks;
        for (int index = 0; index < blocks.Count; index++)
        {
            Block? block = blocks[index];
            if (block?.Code is null || block.IsMissing)
            {
                continue;
            }

            table.Blocks[block.Id] = block.Code.ToString();
        }

        IList<Item> items = world.Items;
        for (int index = 0; index < items.Count; index++)
        {
            Item? item = items[index];
            if (item?.Code is null || item.IsMissing)
            {
                continue;
            }

            table.Items[item.Id] = item.Code.ToString();
        }

        return table;
    }
}

/// <summary>
/// Translates recorded block and item numbers into the current world's by
/// looking the recorded codes up in the current registry.
/// </summary>
internal sealed class DirectorReplayIdRemap : IDirectorReplayIdTranslator
{
    private const string FallbackSolidCode = "game:rock-granite";

    // Decoration that would look worse as a stone cube than as nothing.
    private static readonly string[] SoftBlockHints =
    [
        "leaves", "grass", "plant", "flower", "fern", "crop", "vine", "mushroom", "sapling",
        "berrybush", "reed", "lichen", "moss", "seaweed", "coral", "snowlayer", "looseflints",
        "loosestick", "looseboulder", "loosestones", "looseores", "torch", "glacierice"
    ];

    private readonly IWorldAccessor world;
    private readonly int[] solidMap;
    private readonly int[] fluidMap;
    private readonly int fallbackBlockId;
    private readonly Dictionary<int, AssetLocation> recordedBlocks = new();
    private readonly Dictionary<int, AssetLocation> recordedItems = new();
    private readonly Dictionary<int, int> itemMap = new();
    private readonly SortedSet<string> missingBlocks = new(StringComparer.Ordinal);
    private readonly SortedSet<string> missingItems = new(StringComparer.Ordinal);

    public DirectorReplayIdRemap(DirectorReplayIdTable recorded, IWorldAccessor world)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        this.world = world;
        Dictionary<int, string> blocks = recorded.Blocks ?? new();
        Dictionary<int, string> items = recorded.Items ?? new();
        int size = blocks.Count == 0 ? 1 : Math.Max(1, blocks.Keys.Max() + 1);
        solidMap = new int[size];
        fluidMap = new int[size];
        int fallback = world.GetBlock(new AssetLocation(FallbackSolidCode))?.Id ?? 0;
        fallbackBlockId = fallback;
        bool identity = true;
        foreach ((int id, string code) in blocks)
        {
            if (id < 0 || string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            AssetLocation location = new(code);
            recordedBlocks[id] = location;
            Block? block = world.GetBlock(location);
            if (block is null || block.IsMissing)
            {
                missingBlocks.Add(code);
                solidMap[id] = id == 0 || IsSoft(code) ? 0 : fallback;
                fluidMap[id] = 0;
                identity = false;
                continue;
            }

            solidMap[id] = block.Id;
            fluidMap[id] = block.Id;
            identity &= block.Id == id;
        }

        foreach ((int id, string code) in items)
        {
            if (id < 0 || string.IsNullOrWhiteSpace(code))
            {
                continue;
            }

            AssetLocation location = new(code);
            recordedItems[id] = location;
            Item? item = world.GetItem(location);
            if (item is null || item.IsMissing)
            {
                missingItems.Add(code);
                identity = false;
                continue;
            }

            itemMap[id] = item.Id;
            identity &= item.Id == id;
        }

        IsIdentity = identity;
    }

    /// <summary>True when the recording world numbers everything exactly like this one.</summary>
    public bool IsIdentity { get; }

    public IReadOnlyCollection<string> MissingBlocks => missingBlocks;

    public IReadOnlyCollection<string> MissingItems => missingItems;

    public int Block(int recordedId) => Map(solidMap, recordedId);

    /// <summary>Fluid layer: an unknown fluid becomes nothing rather than a solid placeholder.</summary>
    public int Fluid(int recordedId) => Map(fluidMap, recordedId);

    private static int Map(int[] map, int recordedId)
        => (uint)recordedId < (uint)map.Length ? map[recordedId] : 0;

    public byte[] ItemStack(byte[] recorded)
    {
        using MemoryStream input = new(recorded, writable: false);
        using BinaryReader reader = new(input);
        ItemStack stack = new(reader);
        if (!Fix(stack))
        {
            return [];
        }

        return Unchanged(recorded, stack.ToBytes());
    }

    public byte[] BlockEntity(string className, byte[] recorded)
    {
        using MemoryStream input = new(recorded, writable: false);
        using BinaryReader reader = new(input);
        TreeAttribute tree = new();
        tree.FromBytes(reader);
        FixTree(tree);
        FixChiselledBlock(tree);
        using MemoryStream output = new(recorded.Length + 16);
        using BinaryWriter writer = new(output);
        tree.ToBytes(writer);
        writer.Flush();
        return Unchanged(recorded, output.ToArray());
    }

    public byte[] Entity(byte[] recorded)
    {
        // Entity.ToBytes(forClient: true): the entity number, the watched
        // attribute tree, then position, code and animation data that hold
        // no block or item numbers.
        using MemoryStream input = new(recorded, writable: false);
        using BinaryReader reader = new(input);
        long entityId = reader.ReadInt64();
        TreeAttribute tree = new();
        tree.FromBytes(reader);
        int rest = (int)input.Position;
        FixTree(tree);
        using MemoryStream output = new(recorded.Length + 16);
        using BinaryWriter writer = new(output);
        writer.Write(entityId);
        tree.ToBytes(writer);
        writer.Write(recorded, rest, recorded.Length - rest);
        writer.Flush();
        return Unchanged(recorded, output.ToArray());
    }

    public byte[] Particle(string providerType, byte[] recorded)
    {
        if (providerType.Contains("StackCubeParticles", StringComparison.Ordinal))
        {
            // Three doubles (collision position), then the item stack.
            const int prefix = 3 * sizeof(double);
            if (recorded.Length <= prefix)
            {
                return recorded;
            }

            using MemoryStream input = new(recorded, writable: false);
            using BinaryReader reader = new(input);
            input.Position = prefix;
            ItemStack stack = new(reader);
            int rest = (int)input.Position;
            if (!Fix(stack))
            {
                // Without its item the provider cannot pick a texture.
                return [];
            }

            using MemoryStream output = new(recorded.Length + 16);
            using BinaryWriter writer = new(output);
            writer.Write(recorded, 0, prefix);
            stack.ToBytes(writer);
            writer.Write(recorded, rest, recorded.Length - rest);
            writer.Flush();
            return Unchanged(recorded, output.ToArray());
        }

        if (providerType.Contains("BlockBreakingParticleProps", StringComparison.Ordinal)
            && recorded.Length >= sizeof(int))
        {
            // The damaged block's number is the last field.
            int offset = recorded.Length - sizeof(int);
            int id = BitConverter.ToInt32(recorded, offset);
            int mapped = Block(id);
            if (mapped == id)
            {
                return recorded;
            }

            byte[] copy = (byte[])recorded.Clone();
            BitConverter.TryWriteBytes(copy.AsSpan(offset), mapped);
            return copy;
        }

        return recorded;
    }

    private bool Fix(ItemStack? stack)
    {
        if (stack is null)
        {
            return false;
        }

        if (stack.Class == EnumItemClass.Block)
        {
            if (!recordedBlocks.TryGetValue(stack.Id, out AssetLocation? code))
            {
                return false;
            }

            Block? block = world.GetBlock(code);
            if (block is null || block.IsMissing)
            {
                return false;
            }

            stack.Id = block.Id;
        }
        else
        {
            if (!itemMap.TryGetValue(stack.Id, out int itemId))
            {
                return false;
            }

            stack.Id = itemId;
        }

        stack.ResolveBlockOrItem(world);
        if (stack.Attributes is { } attributes)
        {
            FixTree(attributes);
        }

        return true;
    }

    /// <summary>Translates every item stack stored anywhere inside the tree.</summary>
    private void FixTree(ITreeAttribute tree)
    {
        foreach (KeyValuePair<string, IAttribute> pair in tree)
        {
            switch (pair.Value)
            {
                case ItemstackAttribute stack:
                    if (stack.value is not null && !Fix(stack.value))
                    {
                        stack.value = null;
                    }

                    break;
                case ITreeAttribute child:
                    FixTree(child);
                    break;
                case TreeArrayAttribute children when children.value is not null:
                    foreach (TreeAttribute? item in children.value)
                    {
                        if (item is not null)
                        {
                            FixTree(item);
                        }
                    }

                    break;
            }
        }
    }

    /// <summary>
    /// Chiselled blocks store the numbers of the blocks they are carved from
    /// and decorated with outside any item stack.
    /// </summary>
    private void FixChiselledBlock(ITreeAttribute tree)
    {
        if (!tree.HasAttribute("cuboids"))
        {
            return;
        }

        if (tree["materials"] is IntArrayAttribute { value: { } materials })
        {
            for (int index = 0; index < materials.Length; index++)
            {
                // A carving always needs a real material to draw with.
                int mapped = Block(materials[index]);
                materials[index] = mapped == 0 ? fallbackBlockId : mapped;
            }
        }

        if (tree["decorIds"] is IntArrayAttribute { value: { } decors })
        {
            for (int index = 0; index < decors.Length; index++)
            {
                decors[index] = Block(decors[index]);
            }
        }
    }

    private static bool IsSoft(string code)
    {
        foreach (string hint in SoftBlockHints)
        {
            if (code.Contains(hint, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static byte[] Unchanged(byte[] recorded, byte[] translated)
        => recorded.AsSpan().SequenceEqual(translated) ? recorded : translated;
}
