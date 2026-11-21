namespace VSDirector.Data;

/// <summary>Part of the world kept in a replay snapshot: a vertical band and a square radius.</summary>
public static class DirectorReplayWorldBand
{
    /// <summary>
    /// Chunk rows (inclusive) covering <paramref name="centerY"/> - <paramref name="blocksBelow"/>
    /// to <paramref name="centerY"/> + <paramref name="blocksAbove"/>. Non-positive limits, or a
    /// non-finite center, keep the whole column.
    /// </summary>
    public static (int First, int Last) ChunkRange(
        double centerY,
        int blocksBelow,
        int blocksAbove,
        int columnChunks,
        int chunkSize)
    {
        int last = Math.Max(0, columnChunks - 1);
        if (blocksBelow <= 0 || blocksAbove <= 0 || !double.IsFinite(centerY) || chunkSize <= 0)
        {
            return (0, last);
        }

        int first = (int)Math.Floor((centerY - blocksBelow) / chunkSize);
        int top = (int)Math.Floor((centerY + blocksAbove) / chunkSize);
        return (Math.Clamp(first, 0, last), Math.Clamp(top, 0, last));
    }

    /// <summary>Drops chunks and block entities outside the range. Returns the chunks kept.</summary>
    public static int Trim(
        DirectorReplayWorldSnapshot snapshot,
        double centerX,
        double centerY,
        double centerZ,
        int blocksBelow,
        int blocksAbove,
        int radiusChunks,
        int chunkSize)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        int maxChunkY = 0;
        foreach (DirectorReplayChunkSnapshot chunk in snapshot.Chunks)
        {
            if (chunk is not null)
            {
                maxChunkY = Math.Max(maxChunkY, chunk.ChunkY);
            }
        }

        (int first, int last) = ChunkRange(centerY, blocksBelow, blocksAbove, maxChunkY + 1, chunkSize);
        bool limitRadius = radiusChunks >= 0 && double.IsFinite(centerX) && double.IsFinite(centerZ);
        int centerChunkX = limitRadius ? (int)Math.Floor(centerX / chunkSize) : 0;
        int centerChunkZ = limitRadius ? (int)Math.Floor(centerZ / chunkSize) : 0;

        bool Outside(int chunkX, int chunkY, int chunkZ)
            => chunkY < first
               || chunkY > last
               || limitRadius
                  && (Math.Abs(chunkX - centerChunkX) > radiusChunks
                      || Math.Abs(chunkZ - centerChunkZ) > radiusChunks);

        snapshot.Chunks.RemoveAll(chunk => chunk is null || Outside(chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ));
        snapshot.BlockEntities.RemoveAll(entity => entity is null
            || Outside(
                (int)Math.Floor(entity.X / (double)chunkSize),
                (int)Math.Floor(entity.Y / (double)chunkSize),
                (int)Math.Floor(entity.Z / (double)chunkSize)));
        if (limitRadius)
        {
            snapshot.RadiusChunks = Math.Min(snapshot.RadiusChunks, radiusChunks);
        }
        return snapshot.Chunks.Count;
    }
}
