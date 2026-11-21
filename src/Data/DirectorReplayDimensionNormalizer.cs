namespace VSDirector.Data;

/// <summary>
/// Makes a replay recorded in a modded dimension playable in the world it is
/// being watched in. Vintage Story only streams chunks for the dimension the
/// observer is in, and the supported entity teleport API cannot change an
/// entity's dimension, so a replay from another world recorded in dimension 16
/// or 17 would otherwise wait forever for chunks that never load.
///
/// Positions inside a replay are dimension-local, so rewriting the recorded
/// dimension to the viewing world's dimension keeps every block, actor and
/// sound at exactly the same local coordinates. Nothing is written back to
/// disk: recorded lists are wrapped in read-only translating views, exactly
/// like <see cref="DirectorReplayRetarget"/> does for block and item numbers.
/// </summary>
public static class DirectorReplayDimensionNormalizer
{
    public static void Apply(DirectorReplayDocument document, int dimension)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (dimension < 0)
        {
            return;
        }

        DirectorReplayWorldSnapshot? world = document.WorldSnapshot;
        if (world is not null)
        {
            if (world.Dimension != dimension)
            {
                world.Dimension = dimension;
            }
            if (world.BlockEntities is not null)
            {
                foreach (DirectorReplayBlockEntitySnapshot entity in world.BlockEntities)
                {
                    if (entity is not null && entity.Dimension != dimension)
                    {
                        entity.Dimension = dimension;
                    }
                }
            }
        }

        document.BlockEvents = (document.BlockEvents ?? new()).Project(change =>
        {
            if (change is null || change.Dimension == dimension)
            {
                return change!;
            }
            DirectorReplayBlockEvent copy = change.ShallowCopy();
            copy.Dimension = dimension;
            return copy;
        });

        document.VoiceFrames = (document.VoiceFrames ?? new()).Project(frame =>
        {
            if (frame is null || frame.Dimension == dimension)
            {
                return frame!;
            }
            DirectorReplayVoiceFrame copy = frame.ShallowCopy();
            copy.Dimension = dimension;
            return copy;
        });

        foreach (DirectorReplayActorTrack actor in document.Actors ?? new())
        {
            if (actor?.Samples is null)
            {
                continue;
            }
            actor.Samples = actor.Samples.Project(sample =>
            {
                if (sample is null || sample.Dimension == dimension)
                {
                    return sample!;
                }
                DirectorReplayActorSample copy = sample.ShallowCopy();
                copy.Dimension = dimension;
                return copy;
            });
        }

        document.IncludedPreloadColumns = (document.IncludedPreloadColumns ?? new())
            .Select(column => column.Dimension == dimension ? column : column with { Dimension = dimension })
            .ToList();
        document.ExcludedPreloadColumns = (document.ExcludedPreloadColumns ?? new())
            .Select(column => column.Dimension == dimension ? column : column with { Dimension = dimension })
            .ToList();

        // The optional page cache indexes block events by (X, Y, Z, Dimension).
        // Keep that index in sync with the translated events above.
        document.Pages?.NormalizeBlockIndexDimension(dimension);
    }
}
