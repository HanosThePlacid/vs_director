using System.Globalization;
using System.Reflection;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;
using VSDirector.Data;

namespace VSDirector.Replay;

/// <summary>
/// Watching a replay outside the world it was recorded in: every world keeps
/// a file with its block and item codes, recordings carry a copy, and a
/// replay from elsewhere is translated to the open world as it is read.
/// </summary>
internal sealed partial class DirectorReplayManager
{
    // Close enough to the recording spot that its chunks are loaded anyway.
    private const double TheaterNearbyBlocks = 48d;
    private const int ChunkBlocks = 32;

    private static readonly FieldInfo? ServerInfoField = AccessTools.Field(typeof(ClientMain), "ServerInfo");

    private DirectorReplayWorldFile? currentWorld;

    /// <summary>
    /// Captures the open world's identity and numbering once, and keeps the
    /// world file beside its replays current. The file is what makes replays
    /// recorded before this feature watchable elsewhere.
    /// </summary>
    private DirectorReplayWorldFile? EnsureCurrentWorld(bool forceWrite, out string status)
    {
        status = string.Empty;
        DirectorReplayStore? activeStore = store;
        string worldId = capi.World?.SavegameIdentifier ?? string.Empty;
        if (activeStore is null || worldId.Length == 0 || capi.World!.Blocks.Count <= 1)
        {
            status = "world-not-ready";
            return null;
        }

        if (!forceWrite
            && currentWorld is { } known
            && string.Equals(known.World.WorldId, worldId, StringComparison.Ordinal))
        {
            return known;
        }

        DirectorReplayWorldFile captured = new()
        {
            World = DirectorReplayWorldIdentity.CaptureInfo(capi, DescribeOpenWorld()),
            Ids = DirectorReplayWorldIdentity.CaptureIds(capi.World)
        };
        currentWorld = captured;
        try
        {
            DirectorReplayWorldFile? existing = activeStore.LoadWorldFile();
            DirectorReplayWorldFile toWrite = captured;
            if (existing is not null && !existing.Ids.IsEmpty)
            {
                if (existing.Ids.IsCompatibleWith(captured.Ids))
                {
                    // Keep codes of blocks and items that have since left the
                    // world: older replays may still show them.
                    toWrite = new DirectorReplayWorldFile { World = captured.World, Ids = captured.Ids.Copy() };
                    foreach ((int id, string code) in existing.Ids.Blocks)
                    {
                        toWrite.Ids.Blocks.TryAdd(id, code);
                    }

                    foreach ((int id, string code) in existing.Ids.Items)
                    {
                        toWrite.Ids.Items.TryAdd(id, code);
                    }
                }
                else
                {
                    capi.Logger.Warning(
                        "VS Director: this world's block or item numbers changed since its world file was written; "
                        + "replays recorded before the change may show wrong blocks in other worlds.");
                }

                if (!forceWrite
                    && toWrite.Ids.Blocks.Count == existing.Ids.Blocks.Count
                    && toWrite.Ids.Items.Count == existing.Ids.Items.Count
                    && string.Equals(existing.World.Label, captured.World.Label, StringComparison.Ordinal)
                    && existing.World.MapSizeY == captured.World.MapSizeY)
                {
                    return captured;
                }
            }

            activeStore.SaveWorldFile(toWrite);
            capi.Logger.Notification(
                "VS Director: saved world file for '{0}' ({1} blocks, {2} items).",
                captured.World.Label,
                toWrite.Ids.Blocks.Count,
                toWrite.Ids.Items.Count);
        }
        catch (Exception exception)
        {
            status = exception.Message;
            capi.Logger.Warning("VS Director: world file could not be written: {0}", exception.Message);
        }

        return captured;
    }

    private string DescribeOpenWorld()
    {
        string serverName = string.Empty;
        try
        {
            object? info = ServerInfoField?.GetValue(game);
            serverName = info is null
                ? string.Empty
                : AccessTools.Field(info.GetType(), "ServerName")?.GetValue(info) as string ?? string.Empty;
        }
        catch (Exception)
        {
            // The name is cosmetic.
        }

        serverName = serverName.Trim();
        string worldId = capi.World?.SavegameIdentifier ?? string.Empty;
        string shortId = worldId.Length > 8 ? worldId[..8] : worldId;
        if (capi.IsSinglePlayer)
        {
            return $"Singleplayer {shortId}";
        }

        string host = game.Connectdata?.Host?.Trim() ?? string.Empty;
        if (serverName.Length > 0 && host.Length > 0)
        {
            return $"{serverName} ({host})";
        }

        return serverName.Length > 0 ? serverName : host.Length > 0 ? host : $"Server {shortId}";
    }

    /// <summary>Stamps a new recording with the world it is made in.</summary>
    private void StampRecordingWorld(DirectorReplayDocument document)
    {
        DirectorReplayWorldFile? world = EnsureCurrentWorld(forceWrite: false, out _);
        if (world is null)
        {
            return;
        }

        document.World = world.World.Copy();
        if (config.ReplayEmbedWorldIds)
        {
            document.Ids = world.Ids;
        }
    }

    /// <summary>Writes the open world's file now and reports what it holds.</summary>
    internal string DumpWorldIds()
    {
        DirectorReplayWorldFile? world = EnsureCurrentWorld(forceWrite: true, out string status);
        if (world is null || store is null)
        {
            return Lang.Get("vsdirector:replay-worldfile-failed", status);
        }

        return status.Length > 0
            ? Lang.Get("vsdirector:replay-worldfile-failed", status)
            : Lang.Get(
                "vsdirector:replay-worldfile-saved",
                world.World.Label,
                world.Ids.Blocks.Count,
                world.Ids.Items.Count,
                DirectorReplayWorldFile.PathIn(store.WorldDirectory));
    }

    /// <summary>Lists the worlds that have replays, and what a theater world needs to show them.</summary>
    internal string DescribeReplayWorlds()
    {
        DirectorReplayStore? activeStore = store;
        if (activeStore is null)
        {
            return Lang.Get("vsdirector:world-not-ready");
        }

        var worlds = activeStore.ListWorlds();
        if (worlds.Count == 0)
        {
            return Lang.Get("vsdirector:replay-worlds-empty");
        }

        string current = Path.GetFullPath(activeStore.WorldDirectory);
        List<string> lines = new() { Lang.Get("vsdirector:replay-worlds-header", worlds.Count) };
        foreach ((string directory, DirectorReplayWorldFile file, int replayCount) in worlds)
        {
            bool here = string.Equals(Path.GetFullPath(directory), current, StringComparison.OrdinalIgnoreCase);
            string label = file.World.Label.Length > 0 ? file.World.Label : Path.GetFileName(directory);
            lines.Add(file.Ids.IsEmpty
                ? Lang.Get("vsdirector:replay-worlds-entry-nofile", label, replayCount)
                : Lang.Get(
                    here ? "vsdirector:replay-worlds-entry-current" : "vsdirector:replay-worlds-entry",
                    label,
                    replayCount,
                    file.World.MapSizeY,
                    file.Ids.Blocks.Count));
        }

        lines.Add(Lang.Get("vsdirector:replay-worlds-hint"));
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Makes a replay recorded in another world playable in this one, or
    /// throws with a message that tells the player what is missing.
    /// </summary>
    private void PrepareOtherWorldReplay(DirectorReplayDocument document, string replayPath)
    {
        DirectorReplayWorldFile? sourceFile = null;
        DirectorReplayIdTable? table = document.Ids is { IsEmpty: false } embedded ? embedded : null;
        if (table is null || document.World is null)
        {
            sourceFile = store?.LoadWorldFileFor(replayPath);
            table ??= sourceFile?.Ids is { IsEmpty: false } fromFile ? fromFile : null;
        }

        if (table is null)
        {
            throw new InvalidDataException(Lang.Get("vsdirector:replay-otherworld-no-table"));
        }

        DirectorReplayWorldInfo? source = document.World ?? sourceFile?.World;
        string label = source?.Label is { Length: > 0 } named ? named : Lang.Get("vsdirector:replay-otherworld-unnamed");
        IBlockAccessor accessor = capi.World.BlockAccessor;
        int worldHeight = accessor.MapSizeY;
        bool hasAnchor = DirectorReplayAnchorSelector.TrySelect(
            document,
            0d,
            capi.World.Player?.PlayerUID ?? string.Empty,
            out DirectorReplayAnchorPose anchor);

        int requiredHeight = RequiredWorldHeight(document);
        if (requiredHeight > worldHeight)
        {
            int recorded = source?.MapSizeY > 0 ? source.MapSizeY : document.WorldSnapshot.MapSizeY;
            int recommended = Math.Max((requiredHeight + 63) / 64 * 64, Math.Min(recorded, 2048));
            throw new InvalidDataException(Lang.Get(
                "vsdirector:replay-otherworld-too-low",
                label,
                requiredHeight,
                worldHeight,
                recommended));
        }

        if (hasAnchor
            && (anchor.X < 0d || anchor.Z < 0d || anchor.X >= accessor.MapSizeX || anchor.Z >= accessor.MapSizeZ))
        {
            throw new InvalidDataException(Lang.Get(
                "vsdirector:replay-otherworld-outside-map",
                label,
                Whole(anchor.X),
                Whole(anchor.Z),
                accessor.MapSizeX,
                accessor.MapSizeZ));
        }

        // Rows above this world's ceiling cannot exist here; leaving them in
        // would make the world rebuild wait for chunks that never load.
        DirectorReplayWorldSnapshot snapshot = document.WorldSnapshot;
        snapshot.Chunks.RemoveAll(chunk => chunk is null || chunk.ChunkY * ChunkBlocks >= worldHeight);
        snapshot.BlockEntities.RemoveAll(entity => entity is null || entity.Y >= worldHeight);

        DirectorReplayIdRemap remap = new(table, capi.World);
        DirectorReplayRetarget.Apply(document, remap);

        // A replay recorded in a modded dimension cannot be observed from here:
        // chunks are only streamed for the observer's dimension and the entity
        // teleport API cannot change it. Flatten it into the normal world so the
        // captured content is actually visible. All positions are local to the
        // dimension, so this changes no coordinates.
        int recordedDimension = document.WorldSnapshot?.Dimension ?? 0;
        if (recordedDimension != 0)
        {
            DirectorReplayDimensionNormalizer.Apply(document, 0);
            capi.Logger.Notification(
                "VS Director: replay '{0}' was recorded in dimension {1}; showing it in dimension 0.",
                document.Name,
                recordedDimension);
            capi.ShowChatMessage(Lang.Get("vsdirector:replay-otherworld-dimension", recordedDimension));
        }

        capi.Logger.Notification(
            "VS Director: opened replay '{0}' from '{1}' in this world; {2} block and {3} item types are not available here.",
            document.Name,
            label,
            remap.MissingBlocks.Count,
            remap.MissingItems.Count);
        if (remap.MissingBlocks.Count > 0 || remap.MissingItems.Count > 0)
        {
            capi.Logger.Notification(
                "VS Director: missing block types: {0}",
                string.Join(", ", remap.MissingBlocks.Take(60)));
            capi.Logger.Notification(
                "VS Director: missing item types: {0}",
                string.Join(", ", remap.MissingItems.Take(60)));
            capi.ShowChatMessage(Lang.Get(
                "vsdirector:replay-otherworld-missing",
                label,
                remap.MissingBlocks.Count,
                remap.MissingItems.Count,
                MissingModsHint(source)));
        }
        else
        {
            capi.ShowChatMessage(Lang.Get("vsdirector:replay-otherworld-opened", label));
        }

        if (hasAnchor)
        {
            MoveToRecordingSpot(anchor);
        }
    }

    /// <summary>
    /// Keeps only the world snapshot part inside the replay range sliders (blocks below,
    /// blocks above, radius in chunks) around the recording spot. Runs on every open, so a
    /// smaller range loads faster and uses less memory; it can never add what was not recorded.
    /// </summary>
    private void TrimWorldSnapshotToRange(DirectorReplayDocument document)
    {
        DirectorReplayWorldSnapshot snapshot = document.WorldSnapshot;
        if (snapshot.Chunks.Count == 0)
        {
            return;
        }

        int before = snapshot.Chunks.Count;
        bool hasAnchor = DirectorReplayAnchorSelector.TrySelect(
            document,
            0d,
            capi.World.Player?.PlayerUID ?? string.Empty,
            out DirectorReplayAnchorPose anchor);
        double centerX = hasAnchor ? anchor.X : (snapshot.CenterChunkX + 0.5d) * ChunkBlocks;
        double centerZ = hasAnchor ? anchor.Z : (snapshot.CenterChunkZ + 0.5d) * ChunkBlocks;
        double centerY = hasAnchor ? anchor.Y : double.NaN;
        int kept = DirectorReplayWorldBand.Trim(
            snapshot,
            centerX,
            centerY,
            centerZ,
            config.ReplayRangeBelowBlocks,
            config.ReplayRangeAboveBlocks,
            config.ReplaySnapshotRadiusChunks,
            ChunkBlocks);
        capi.Logger.Notification(
            "VS Director: replay range {0} below / {1} above y={2}, radius {3} chunks: kept {4} of {5} snapshot chunks.",
            config.ReplayRangeBelowBlocks,
            config.ReplayRangeAboveBlocks,
            hasAnchor ? Whole(anchor.Y) : "?",
            config.ReplaySnapshotRadiusChunks,
            kept,
            before);
    }

    /// <summary>
    /// The lowest world height that holds everything the snapshot shows. A
    /// chunk stored as a single run is uniform (open air, or solid rock deep
    /// underground), so only mixed chunks decide where the content ends.
    /// </summary>
    private static int RequiredWorldHeight(DirectorReplayDocument document)
    {
        int top = 0;
        foreach (DirectorReplayChunkSnapshot chunk in document.WorldSnapshot.Chunks)
        {
            if (chunk is null || (chunk.SolidRuns?.Count ?? 0) <= 1 && (chunk.FluidRuns?.Count ?? 0) <= 1)
            {
                continue;
            }

            top = Math.Max(top, (chunk.ChunkY + 1) * ChunkBlocks);
        }

        return top;
    }

    private string MissingModsHint(DirectorReplayWorldInfo? source)
    {
        if (source?.Mods is not { Count: > 0 })
        {
            return string.Empty;
        }

        HashSet<string> loaded = new(StringComparer.OrdinalIgnoreCase);
        foreach (Mod mod in capi.ModLoader.Mods)
        {
            if (mod?.Info?.ModID is { Length: > 0 } id)
            {
                loaded.Add(id);
            }
        }

        string[] missing = source.Mods
            .Select(entry => entry.Split('@')[0])
            .Where(id => id.Length > 0 && !loaded.Contains(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
        return missing.Length == 0
            ? string.Empty
            : Lang.Get("vsdirector:replay-otherworld-missing-mods", string.Join(", ", missing));
    }

    /// <summary>
    /// A replay rebuilds the terrain around where it was recorded, so those
    /// chunks have to be loaded. In a theater world the player is simply
    /// moved there; elsewhere the player is told where to go.
    /// </summary>
    private void MoveToRecordingSpot(DirectorReplayAnchorPose anchor)
    {
        if (capi.World.Player?.Entity is not { } player)
        {
            return;
        }

        double dx = player.Pos.X - anchor.X;
        double dz = player.Pos.Z - anchor.Z;
        if (player.Pos.Dimension == anchor.Dimension
            && dx * dx + dz * dz <= TheaterNearbyBlocks * TheaterNearbyBlocks)
        {
            return;
        }

        EnumGameMode mode = capi.World.Player.WorldData?.CurrentGameMode ?? EnumGameMode.Survival;
        // Only in a singleplayer world: moving the player on a shared server
        // is not something opening a replay should do.
        bool mayTeleport = config.ReplayTheaterTeleport
            && capi.IsSinglePlayer
            && mode is EnumGameMode.Creative or EnumGameMode.Spectator;
        if (!mayTeleport)
        {
            capi.ShowChatMessage(Lang.Get(
                "vsdirector:replay-otherworld-far",
                Whole(anchor.X),
                Whole(anchor.Y),
                Whole(anchor.Z)));
            return;
        }

        // "=" marks absolute block coordinates; plain numbers are relative
        // to the world's spawn point.
        capi.SendChatMessage($"/tp ={Whole(anchor.X)} ={Whole(anchor.Y + 1d)} ={Whole(anchor.Z)}");
        capi.ShowChatMessage(Lang.Get(
            "vsdirector:replay-otherworld-teleported",
            Whole(anchor.X),
            Whole(anchor.Y),
            Whole(anchor.Z)));
    }

    private static string Whole(double value)
        => ((long)Math.Floor(value)).ToString(CultureInfo.InvariantCulture);
}
