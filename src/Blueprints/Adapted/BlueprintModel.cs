// Adapted from Buildwright 9b15446; covered by the project MIT LICENSE.
// Mesh/capture portions: Copyright (c) 2026 Jeffrey Lueken (Venah), Fieldwright (MIT).
// See LICENSE and licenses/Fieldwright-MIT.txt.
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VSDirector.Blueprints;

namespace VSDirector.Blueprints;

public enum BlueprintFileKind
{
    NativeJson,
    BuildwrightZip
}

public enum MirrorAxis
{
    None,
    X,
    Y,
    Z
}

public enum MatchingMode
{
    Loose,
    Medium,
    Strict
}

public enum GhostDisplayMode
{
    Full,
    Missing,
    Wrong,
    Difference,
    Layer
}

public enum DiffKind
{
    Missing,
    Matched,
    Wrong,
    Extra,
    Unknown
}

public sealed class Int3
{
    [JsonProperty("x")] public int X { get; set; }
    [JsonProperty("y")] public int Y { get; set; }
    [JsonProperty("z")] public int Z { get; set; }

    public Int3() { }
    public Int3(int x, int y, int z) { X = x; Y = y; Z = z; }
    public Int3 Copy() => new(X, Y, Z);
    public override string ToString() => $"({X},{Y},{Z})";
}

public sealed class BlueprintMetadata
{
    [JsonProperty("formatVersion")] public int FormatVersion { get; set; } = 1;
    [JsonProperty("name")] public string Name { get; set; } = "Unnamed";
    [JsonProperty("author")] public string Author { get; set; } = "Unknown Author";
    [JsonProperty("description")] public string Description { get; set; } = string.Empty;
    [JsonProperty("gameVersion")] public string GameVersion { get; set; } = string.Empty;
    [JsonProperty("dimensions")] public Int3 Dimensions { get; set; } = new();
    [JsonProperty("anchorOffset")] public Int3 AnchorOffset { get; set; } = new();
    [JsonProperty("anchorFace", NullValueHandling = NullValueHandling.Ignore)] public string? AnchorFace { get; set; }
    [JsonProperty("dependencies")] public List<string> Dependencies { get; set; } = new();
    [JsonProperty("capabilities")] public List<string> Capabilities { get; set; } = new();
    [JsonProperty("blockCount")] public int BlockCount { get; set; }
    [JsonProperty("materialCount")] public int MaterialCount { get; set; }
    [JsonProperty("schematicSha256")] public string? SchematicSha256 { get; set; }
}

public sealed class BlockEntityRecord
{
    [JsonProperty("position")] public Int3 Position { get; set; } = new();
    [JsonProperty("class")] public string? EntityClass { get; set; }
    [JsonProperty("treeAscii85")] public string TreeAscii85 { get; set; } = string.Empty;
    [JsonProperty("orientation")] public string? Orientation { get; set; }
    [JsonProperty("compat", NullValueHandling = NullValueHandling.Ignore)] public List<BuildwrightCompatPayload> CompatData { get; set; } = new();
}

public sealed class ChiselVoxelData
{
    [JsonProperty("position")] public Int3 Position { get; set; } = new();
    [JsonProperty("blockIds")] public int[] BlockIds { get; set; } = Array.Empty<int>();
    [JsonProperty("voxelCuboids")] public uint[] VoxelCuboids { get; set; } = Array.Empty<uint>();
    [JsonProperty("originalVoxelCuboids")] public uint[] OriginalVoxelCuboids { get; set; } = Array.Empty<uint>();
    [JsonProperty("decorIds", NullValueHandling = NullValueHandling.Ignore)] public int[]? DecorIds { get; set; }
    [JsonProperty("decorRotations")] public int DecorRotations { get; set; }
    [JsonProperty("rotation")] public int Rotation { get; set; }
}

public sealed class BlueprintDocument
{
    public BlueprintMetadata Metadata { get; set; } = new();
    public JObject Schematic { get; set; } = new();
    public List<BlockEntityRecord> BlockEntities { get; set; } = new();
    public List<ChiselVoxelData> ChiselData { get; set; } = new();

    public string DisplayName => Metadata.Name;
}
