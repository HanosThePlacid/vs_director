using VSDirector.Data;

/// <summary>
/// A replay recorded in one world must show the same blocks and items when it
/// is opened in a world that numbers them differently.
/// </summary>
internal static class ReplayRetargetChecks
{
    private sealed class ShiftTranslator : IDirectorReplayIdTranslator
    {
        public int ItemCalls;

        public int Block(int recordedId) => recordedId == 0 ? 0 : recordedId + 100;

        public int Fluid(int recordedId) => recordedId == 0 ? 0 : recordedId + 200;

        public byte[] ItemStack(byte[] recorded)
        {
            ItemCalls++;
            // 7 marks an item that does not exist in the watching world.
            return recorded[0] == 7 ? [] : recorded[0] == 5 ? recorded : [(byte)(recorded[0] + 1)];
        }

        public byte[] BlockEntity(string className, byte[] recorded)
            => className == "broken" ? throw new InvalidDataException("unreadable") : [.. recorded, 1];

        public byte[] Entity(byte[] recorded) => [.. recorded, 2];

        public byte[] Particle(string providerType, byte[] recorded) => [.. recorded, 3];
    }

    internal static void Run()
    {
        string folder = Path.Combine(Path.GetTempPath(), "director-retarget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            CheckWorldFile(folder);
            DirectorReplayDocument source = Build();
            string path = Path.Combine(folder, "other.vsdreplay.gz");
            DirectorReplayIO.Save(path, source);

            DirectorReplayDocument loaded = DirectorReplayIO.Load(path);
            Require(loaded.World?.Label == "Test server" && loaded.World.MapSizeY == 2048, "World details were not saved with the replay.");
            Require(loaded.Ids?.Blocks[2] == "game:rock-granite" && loaded.Ids.Items[9] == "game:stick", "Block and item names were not saved with the replay.");
            CheckTranslation(loaded, "in-memory");

            DirectorReplayDocument paged = DirectorReplayIO.LoadPagedAsync(path).GetAwaiter().GetResult();
            Require(paged.Ids?.Blocks[2] == "game:rock-granite", "Paged open lost the block names.");
            Require(paged.World?.WorldId == "recorded-world", "Paged open lost the world details.");
            CheckTranslation(paged, "paged");

            // The recorded document itself must stay as recorded.
            Require(source.WorldSnapshot.Chunks[0].SolidRuns[0].BlockId == 2, "Translation changed the source document.");
        }
        finally { Directory.Delete(folder, true); }
    }

    private static DirectorReplayDocument Build()
    {
        DirectorReplayDocument document = new()
        {
            Name = "Other world",
            WorldId = "recorded-world",
            DurationSeconds = 10,
            World = new DirectorReplayWorldInfo { WorldId = "recorded-world", Label = "Test server", MapSizeY = 2048 },
            Ids = new DirectorReplayIdTable
            {
                Blocks = { [0] = "game:air", [2] = "game:rock-granite", [3] = "game:water-still-7" },
                Items = { [9] = "game:stick" }
            }
        };
        DirectorReplayActorTrack actor = new()
        {
            ActorKey = "a",
            EntityId = 42,
            EntityCode = "game:player",
            InitialState = [10]
        };
        actor.Samples.Add(new() { TimeSeconds = 0, RightHandItem = [1], LeftHandItem = [5], EquipmentItems = [[5], [1]], BackpackItems = [[7]] });
        actor.Samples.Add(new() { TimeSeconds = 1 });
        actor.States.Add(new() { TimeSeconds = 0.5, Data = [20] });
        document.Actors.Add(actor);
        document.WorldSnapshot.Chunks.Add(new()
        {
            SolidRuns = [new() { BlockId = 2, Length = 100 }, new() { BlockId = 0, Length = 32668 }],
            FluidRuns = [new() { BlockId = 3, Length = 4 }, new() { BlockId = 0, Length = 32764 }]
        });
        document.WorldSnapshot.BlockEntities.Add(new() { X = 1, Y = 2, Z = 3, ClassName = "chest", Data = [30] });
        document.WorldSnapshot.BlockEntities.Add(new() { X = 4, Y = 5, Z = 6, ClassName = "broken", Data = [31] });
        document.BlockEvents.Add(new() { TimeSeconds = 1, X = 1, Y = 1, Z = 1, SolidBlockId = 2, FluidBlockId = 3, BlockEntityClass = "chest", BlockEntityData = [40] });
        document.BlockEvents.Add(new() { TimeSeconds = 2, X = 1, Y = 1, Z = 1 });
        document.ParticleEvents.Add(new() { TimeSeconds = 1, Kind = DirectorReplayParticleKind.Provider, ProviderType = "p", ProviderData = [50] });
        return document;
    }

    private static void CheckTranslation(DirectorReplayDocument document, string mode)
    {
        ShiftTranslator translator = new();
        DirectorReplayRetarget.Apply(document, translator);
        Require(document.IsRetargeted, mode + ": the replay was not marked as translated.");

        DirectorReplayChunkSnapshot chunk = document.WorldSnapshot.Chunks[0];
        Require(chunk.SolidRuns.Count == 2 && chunk.SolidRuns[0].BlockId == 102 && chunk.SolidRuns[0].Length == 100, mode + ": solid blocks were not translated.");
        Require(chunk.SolidRuns[1].BlockId == 0 && chunk.SolidRuns[1].Length == 32668, mode + ": air must stay air.");
        Require(chunk.FluidRuns[0].BlockId == 203 && chunk.FluidRuns[1].BlockId == 0, mode + ": fluids were not translated as fluids.");

        Require(document.WorldSnapshot.BlockEntities[0].Data.SequenceEqual(new byte[] { 30, 1 }), mode + ": block entity contents were not translated.");
        Require(document.WorldSnapshot.BlockEntities[1].Data.SequenceEqual(new byte[] { 31 }), mode + ": an unreadable block entity must stay as recorded.");

        DirectorReplayBlockEvent change = document.BlockEvents[0];
        Require(change.SolidBlockId == 102 && change.FluidBlockId == 203 && change.BlockEntityData.SequenceEqual(new byte[] { 40, 1 }), mode + ": a block change was not translated.");
        Require(change.X == 1 && change.TimeSeconds == 1, mode + ": translating a block change lost its other fields.");
        Require(document.BlockEvents[1].SolidBlockId == 0, mode + ": an empty block change was altered.");
        Require(document.ParticleEvents[0].ProviderData.SequenceEqual(new byte[] { 50, 3 }), mode + ": particle contents were not translated.");

        DirectorReplayActorTrack actor = document.Actors[0];
        Require(actor.RecordedElsewhere && actor.EntityId == 42, mode + ": the actor must keep its recorded number but be marked foreign.");
        Require(actor.InitialState.SequenceEqual(new byte[] { 10, 2 }), mode + ": the actor's initial state was not translated.");
        Require(actor.States[0].Data.SequenceEqual(new byte[] { 20, 2 }) && actor.States[0].TimeSeconds == 0.5, mode + ": an actor state was not translated.");

        DirectorReplayActorSample sample = actor.Samples[0];
        Require(sample.RightHandItem.SequenceEqual(new byte[] { 2 }), mode + ": the held item was not translated.");
        Require(sample.LeftHandItem.SequenceEqual(new byte[] { 5 }), mode + ": an item that needs no translation changed.");
        Require(sample.EquipmentItems.Count == 2 && sample.EquipmentItems[0][0] == 5 && sample.EquipmentItems[1][0] == 2, mode + ": worn items were not translated in place.");
        Require(sample.BackpackItems.Count == 1 && sample.BackpackItems[0].Length == 0, mode + ": an item missing in this world must become an empty slot.");
        Require(actor.Samples[1].TimeSeconds == 1 && actor.Samples[1].RightHandItem is not { Length: > 0 }, mode + ": a sample without items was altered.");

        int calls = translator.ItemCalls;
        for (int repeat = 0; repeat < 50; repeat++)
        {
            _ = actor.Samples[0];
        }

        Require(translator.ItemCalls == calls, mode + ": reading the same sample again translated its items again.");

        // A second pass must not translate numbers that are already translated.
        DirectorReplayRetarget.Apply(document, translator);
        Require(document.WorldSnapshot.Chunks[0].SolidRuns[0].BlockId == 102, mode + ": the replay was translated twice.");
    }

    private static void CheckWorldFile(string folder)
    {
        string path = DirectorReplayWorldFile.PathIn(folder);
        Require(DirectorReplayWorldFile.TryLoad(path) is null, "A missing world file must read as absent.");
        DirectorReplayWorldFile file = new()
        {
            World = new DirectorReplayWorldInfo { WorldId = "w", Label = "Server (host)", MapSizeY = 2048, Mods = ["game@1.22.7"] },
            Ids = new DirectorReplayIdTable { Blocks = { [1] = "game:soil", [14082] = "mod:thing" }, Items = { [3] = "game:flint" } }
        };
        DirectorReplayWorldFile.Save(path, file);
        DirectorReplayWorldFile.Save(path, file);
        DirectorReplayWorldFile? loaded = DirectorReplayWorldFile.TryLoad(path);
        Require(loaded is not null && loaded.World.Label == "Server (host)" && loaded.World.MapSizeY == 2048 && loaded.World.Mods.Single() == "game@1.22.7", "World details did not survive saving.");
        Require(loaded!.Ids.Blocks.Count == 2 && loaded.Ids.Blocks[14082] == "mod:thing" && loaded.Ids.Items[3] == "game:flint", "Block and item names did not survive saving.");
        Require(Directory.GetFiles(folder).Length == 1, "Saving the world file left temporary files behind.");

        DirectorReplayIdTable grown = loaded.Ids.Copy();
        grown.Blocks[20000] = "mod:new";
        Require(loaded.Ids.IsCompatibleWith(grown) && grown.IsCompatibleWith(loaded.Ids), "A world that only gained blocks must still match its file.");
        grown.Blocks[1] = "game:sand";
        Require(!loaded.Ids.IsCompatibleWith(grown), "A renumbered world must not match its old file.");

        File.WriteAllText(path, "not a gzip file");
        Require(DirectorReplayWorldFile.TryLoad(path) is null, "A damaged world file must read as absent.");
        File.Delete(path);
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
}
