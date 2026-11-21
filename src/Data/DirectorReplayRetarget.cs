using System.Runtime.CompilerServices;

namespace VSDirector.Data;

/// <summary>
/// Translates what a replay recorded in one world into the numbers of the
/// world it is being watched in. Every method must return its argument
/// unchanged (the same instance for arrays) when nothing needs translating,
/// and must never modify a recorded array in place.
/// </summary>
public interface IDirectorReplayIdTranslator
{
    /// <summary>A block number from the solid layer.</summary>
    int Block(int recordedId);

    /// <summary>A block number from the fluid layer.</summary>
    int Fluid(int recordedId);

    /// <summary>A serialized item stack (hand, equipment or backpack slot).</summary>
    byte[] ItemStack(byte[] recorded);

    /// <summary>A serialized block entity attribute tree.</summary>
    byte[] BlockEntity(string className, byte[] recorded);

    /// <summary>A serialized entity (initial or full state).</summary>
    byte[] Entity(byte[] recorded);

    /// <summary>A serialized particle provider.</summary>
    byte[] Particle(string providerType, byte[] recorded);
}

/// <summary>
/// Makes a replay from another world playable in the current one by wrapping
/// its recorded lists in translating views. Nothing is rewritten on disk and
/// nothing is translated before it is read, so opening stays as cheap as
/// opening a replay of the current world.
/// </summary>
public static class DirectorReplayRetarget
{
    public static void Apply(DirectorReplayDocument document, IDirectorReplayIdTranslator translator)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(translator);
        if (document.IsRetargeted)
        {
            return;
        }

        // Page arrays live in the page cache, so a translated payload is
        // remembered for as long as its recorded source stays cached.
        var items = new BlobCache(translator.ItemStack);
        var entities = new BlobCache(translator.Entity);
        var samples = new ConditionalWeakTable<DirectorReplayActorSample, DirectorReplayActorSample>();

        DirectorReplayBlockRun MapSolidRun(DirectorReplayBlockRun run)
        {
            if (run is null)
            {
                return run!;
            }

            int mapped = translator.Block(run.BlockId);
            return mapped == run.BlockId ? run : new DirectorReplayBlockRun { BlockId = mapped, Length = run.Length };
        }

        DirectorReplayBlockRun MapFluidRun(DirectorReplayBlockRun run)
        {
            if (run is null)
            {
                return run!;
            }

            int mapped = translator.Fluid(run.BlockId);
            return mapped == run.BlockId ? run : new DirectorReplayBlockRun { BlockId = mapped, Length = run.Length };
        }

        DirectorReplayWorldSnapshot world = document.WorldSnapshot ??= new DirectorReplayWorldSnapshot();
        foreach (DirectorReplayChunkSnapshot chunk in world.Chunks)
        {
            if (chunk is null)
            {
                continue;
            }

            chunk.SolidRuns = (chunk.SolidRuns ?? new()).Project(MapSolidRun);
            chunk.FluidRuns = (chunk.FluidRuns ?? new()).Project(MapFluidRun);
        }

        foreach (DirectorReplayBlockEntitySnapshot snapshot in world.BlockEntities)
        {
            if (snapshot is null)
            {
                continue;
            }

            Func<byte[]>? reader = snapshot.DataReader;
            byte[] loaded = reader is null ? snapshot.Data : [];
            string className = snapshot.ClassName ?? string.Empty;
            byte[]? translated = null;
            object gate = new();
            snapshot.DataReader = () =>
            {
                lock (gate)
                {
                    return translated ??= TranslateBlob(
                        reader is null ? loaded : reader(),
                        data => translator.BlockEntity(className, data));
                }
            };
        }

        var blockEntities = new ConditionalWeakTable<byte[], byte[]>();
        document.BlockEvents = (document.BlockEvents ?? new()).Project(change =>
        {
            if (change is null)
            {
                return change!;
            }

            int solid = translator.Block(change.SolidBlockId);
            int fluid = translator.Fluid(change.FluidBlockId);
            byte[] data = change.BlockEntityData ?? [];
            byte[] mappedData = data;
            if (data.Length > 0)
            {
                string className = change.BlockEntityClass ?? string.Empty;
                mappedData = blockEntities.GetValue(
                    data,
                    source => TranslateBlob(source, value => translator.BlockEntity(className, value)));
            }

            if (solid == change.SolidBlockId && fluid == change.FluidBlockId && ReferenceEquals(mappedData, data))
            {
                return change;
            }

            DirectorReplayBlockEvent copy = change.ShallowCopy();
            copy.SolidBlockId = solid;
            copy.FluidBlockId = fluid;
            copy.BlockEntityData = mappedData;
            return copy;
        });

        var particles = new ConditionalWeakTable<byte[], byte[]>();
        document.ParticleEvents = (document.ParticleEvents ?? new()).Project(particle =>
        {
            if (particle is null
                || particle.Kind != DirectorReplayParticleKind.Provider
                || particle.ProviderData is not { Length: > 0 } data)
            {
                return particle!;
            }

            string providerType = particle.ProviderType ?? string.Empty;
            byte[] mapped = particles.GetValue(
                data,
                source => TranslateBlob(source, value => translator.Particle(providerType, value)));
            if (ReferenceEquals(mapped, data))
            {
                return particle;
            }

            DirectorReplayParticleEvent copy = particle.ShallowCopy();
            copy.ProviderData = mapped;
            return copy;
        });

        foreach (DirectorReplayActorTrack actor in document.Actors)
        {
            if (actor is null)
            {
                continue;
            }

            // Entity numbers are per world. The recorded number still names
            // the actor inside the replay (mounts, follow targets), but it
            // must never select a live entity that happens to share it here.
            actor.RecordedElsewhere = true;

            Func<byte[]>? initialReader = actor.InitialStateReader;
            byte[] initialLoaded = initialReader is null ? actor.InitialState : [];
            byte[]? initialTranslated = null;
            object initialGate = new();
            actor.InitialStateReader = () =>
            {
                lock (initialGate)
                {
                    return initialTranslated ??= entities.Get(
                        initialReader is null ? initialLoaded : initialReader());
                }
            };

            actor.States = (actor.States ?? new()).Project(state =>
            {
                if (state is null || state.Data is not { Length: > 0 } data)
                {
                    return state!;
                }

                byte[] mapped = entities.Get(data);
                return ReferenceEquals(mapped, data) ? state : state.WithData(mapped);
            });

            actor.Samples = (actor.Samples ?? new()).Project(sample =>
            {
                if (sample is null || !HasItems(sample))
                {
                    return sample!;
                }

                return samples.GetValue(sample, source => TranslateSample(source, items));
            });
        }

        document.IsRetargeted = true;
    }

    private static bool HasItems(DirectorReplayActorSample sample)
        => sample.RightHandItem is { Length: > 0 }
            || sample.LeftHandItem is { Length: > 0 }
            || sample.EquipmentItems is { Count: > 0 }
            || sample.BackpackItems is { Count: > 0 };

    private static DirectorReplayActorSample TranslateSample(DirectorReplayActorSample source, BlobCache items)
    {
        byte[] right = items.Get(source.RightHandItem);
        byte[] left = items.Get(source.LeftHandItem);
        List<byte[]> equipment = TranslateItems(source.EquipmentItems, items);
        List<byte[]> backpack = TranslateItems(source.BackpackItems, items);
        if (ReferenceEquals(right, source.RightHandItem)
            && ReferenceEquals(left, source.LeftHandItem)
            && ReferenceEquals(equipment, source.EquipmentItems)
            && ReferenceEquals(backpack, source.BackpackItems))
        {
            return source;
        }

        DirectorReplayActorSample copy = source.ShallowCopy();
        copy.RightHandItem = right;
        copy.LeftHandItem = left;
        copy.EquipmentItems = equipment;
        copy.BackpackItems = backpack;
        return copy;
    }

    private static List<byte[]> TranslateItems(List<byte[]>? source, BlobCache items)
    {
        if (source is not { Count: > 0 })
        {
            return source!;
        }

        List<byte[]>? result = null;
        for (int index = 0; index < source.Count; index++)
        {
            byte[] original = source[index];
            byte[] mapped = items.Get(original);
            if (result is null && !ReferenceEquals(mapped, original))
            {
                result = new List<byte[]>(source.Count);
                for (int copied = 0; copied < index; copied++)
                {
                    result.Add(source[copied]);
                }
            }

            result?.Add(mapped);
        }

        return result ?? source;
    }

    private static byte[] TranslateBlob(byte[]? recorded, Func<byte[], byte[]> translate)
    {
        if (recorded is not { Length: > 0 })
        {
            return recorded ?? [];
        }

        try
        {
            return translate(recorded) ?? recorded;
        }
        catch (Exception)
        {
            // An unreadable payload is left as recorded: the playback code
            // already tolerates payloads it cannot apply.
            return recorded;
        }
    }

    private sealed class BlobCache(Func<byte[], byte[]> translate)
    {
        private readonly ConditionalWeakTable<byte[], byte[]> translated = new();

        public byte[] Get(byte[]? recorded)
            => recorded is not { Length: > 0 }
                ? recorded ?? []
                : translated.GetValue(recorded, source => TranslateBlob(source, translate));
    }
}
