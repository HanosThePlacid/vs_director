using System.IO.Compression;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace VSDirector.Data;

/// <summary>
/// Describes the world a replay was recorded in. Block and item numbers are
/// assigned per world, so a replay can only be shown elsewhere when the
/// recording world's name table travels with it.
/// </summary>
public sealed class DirectorReplayWorldInfo
{
    public string WorldId { get; set; } = string.Empty;

    /// <summary>Human readable label: the server name or address, or the singleplayer world name.</summary>
    public string Label { get; set; } = string.Empty;

    public bool Singleplayer { get; set; }

    public string GameVersion { get; set; } = string.Empty;

    public int Seed { get; set; }

    public int MapSizeX { get; set; }

    public int MapSizeY { get; set; }

    public int MapSizeZ { get; set; }

    public int SeaLevel { get; set; }

    public string PlayStyle { get; set; } = string.Empty;

    public string WorldType { get; set; } = string.Empty;

    /// <summary>Loaded mods as "modid@version", sorted.</summary>
    public List<string> Mods { get; set; } = new();

    public DateTimeOffset CapturedUtc { get; set; }

    public DirectorReplayWorldInfo Copy()
        => new()
        {
            WorldId = WorldId,
            Label = Label,
            Singleplayer = Singleplayer,
            GameVersion = GameVersion,
            Seed = Seed,
            MapSizeX = MapSizeX,
            MapSizeY = MapSizeY,
            MapSizeZ = MapSizeZ,
            SeaLevel = SeaLevel,
            PlayStyle = PlayStyle,
            WorldType = WorldType,
            Mods = Mods is null ? new List<string>() : new List<string>(Mods),
            CapturedUtc = CapturedUtc
        };
}

/// <summary>Block and item numbers of one world, by code ("game:rock-granite").</summary>
public sealed class DirectorReplayIdTable
{
    public Dictionary<int, string> Blocks { get; set; } = new();

    public Dictionary<int, string> Items { get; set; } = new();

    public bool IsEmpty => (Blocks?.Count ?? 0) == 0 && (Items?.Count ?? 0) == 0;

    /// <summary>The table is immutable once captured, so copies may share it.</summary>
    public DirectorReplayIdTable Copy()
        => new()
        {
            Blocks = Blocks is null ? new Dictionary<int, string>() : new Dictionary<int, string>(Blocks),
            Items = Items is null ? new Dictionary<int, string>() : new Dictionary<int, string>(Items)
        };

    /// <summary>
    /// True when every number both tables know resolves to the same code.
    /// Numbers only one side knows do not conflict: a savegame appends new
    /// blocks and never renumbers the old ones.
    /// </summary>
    public bool IsCompatibleWith(DirectorReplayIdTable? other)
    {
        if (other is null)
        {
            return false;
        }

        return Agree(Blocks, other.Blocks) && Agree(Items, other.Items);
    }

    private static bool Agree(Dictionary<int, string>? left, Dictionary<int, string>? right)
    {
        if (left is null || right is null)
        {
            return true;
        }

        Dictionary<int, string> small = left.Count <= right.Count ? left : right;
        Dictionary<int, string> large = ReferenceEquals(small, left) ? right : left;
        foreach ((int id, string code) in small)
        {
            if (large.TryGetValue(id, out string? other)
                && !string.Equals(code, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>The per-world file stored beside that world's replays.</summary>
public sealed class DirectorReplayWorldFile
{
    public const string FileName = "world.vsdworld.json.gz";

    public const int CurrentVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public int Version { get; set; } = CurrentVersion;

    public DirectorReplayWorldInfo World { get; set; } = new();

    public DirectorReplayIdTable Ids { get; set; } = new();

    public static string PathIn(string worldDirectory) => Path.Combine(worldDirectory, FileName);

    public static DirectorReplayWorldFile? TryLoad(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return null;
            }

            using FileStream file = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using GZipStream gzip = new(file, CompressionMode.Decompress);
            DirectorReplayWorldFile? loaded = JsonSerializer.Deserialize<DirectorReplayWorldFile>(gzip, Options);
            if (loaded is null)
            {
                return null;
            }

            loaded.World ??= new DirectorReplayWorldInfo();
            loaded.Ids ??= new DirectorReplayIdTable();
            loaded.Ids.Blocks ??= new Dictionary<int, string>();
            loaded.Ids.Items ??= new Dictionary<int, string>();
            return loaded;
        }
        catch (Exception exception) when (exception is IOException
            or UnauthorizedAccessException
            or JsonException
            or InvalidDataException)
        {
            return null;
        }
    }

    public static void Save(string path, DirectorReplayWorldFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporary = fullPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (GZipStream gzip = new(stream, CompressionLevel.Optimal))
            {
                JsonSerializer.Serialize(gzip, file, Options);
            }

            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporary);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
