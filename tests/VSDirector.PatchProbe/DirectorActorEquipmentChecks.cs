using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using VSDirector;
using VSDirector.Data;

internal static class DirectorActorEquipmentChecks
{
    internal static void Run()
    {
        var assembly = typeof(VSDirectorModSystem).Assembly;
        var runtimeType = assembly.GetType("VSDirector.Runtime.DirectorCreationRuntime", true)!;
        void Check(bool value, string message) { if (!value) throw new Exception(message); }
        var appearanceEntity=new Vintagestory.API.Common.EntityAgent();
        appearanceEntity.World=ProbeDispatchProxy.Create<IWorldAccessor>((m,_)=>m.Name=="get_Side"?EnumAppSide.Client:ProbeDispatchProxy.DefaultValue(m.ReturnType));
        AccessTools.Property(typeof(Vintagestory.API.Common.Entities.Entity),"Properties").SetValue(appearanceEntity,
            new Vintagestory.API.Common.Entities.EntityProperties{Client=new Vintagestory.API.Common.Entities.EntityClientProperties([],null!)});
        appearanceEntity.WatchedAttributes.SetString("voicetype","altoflute");
        appearanceEntity.WatchedAttributes.SetString("voicepitch","high");
        appearanceEntity.WatchedAttributes.SetString("characterClass","hunter");
        var captured=TreeAttribute.CreateFromBytes((byte[])AccessTools.Method(runtimeType,"CaptureAppearanceData").Invoke(null,[appearanceEntity])!);
        Check(captured.GetString("voicetype")=="altoflute" && captured.GetString("voicepitch")=="high" && captured.GetString("characterClass")=="hunter",
            "Actor appearance lost native voice or class metadata");
        var item = new Item { ItemId = 42, Code = new AssetLocation("game:equipment-probe") };
        var stack = new ItemStack(item, 3); stack.Attributes.SetString("variant", "preserve-me");
        var baseTexture = new CompositeTexture(new AssetLocation("game:skin"))
        { Baked = new BakedCompositeTexture { TextureSubId = 17, BakedName = new AssetLocation("game:skin"), TextureFilenames = [new AssetLocation("game:skin")] } };
        baseTexture.Baked.BakedVariants = [baseTexture.Baked];
        var copied = (CompositeTexture)AccessTools.Method(runtimeType, "CloneAppearanceTexture").Invoke(null, [baseTexture])!;
        copied.Baked.TextureSubId = 99; copied.Baked.BakedName.Path = "private-skin";
        Check(baseTexture.Baked.TextureSubId == 17 && baseTexture.Baked.BakedName.Path == "skin"
            && ReferenceEquals(copied.Baked.BakedVariants[0], copied.Baked), "Skin editing changed live atlas metadata or broke variant references");

        string directory = Path.Combine(Path.GetTempPath(), "director-equipment-" + Guid.NewGuid().ToString("N"));
        try
        {
            var session = new DirectorCreationSession(new(directory));
            var hand = new CreationPart { Name = "RightHand", Group = true };
            var model = new CreationModel { Parts = [hand] };
            var skin = captured; skin.SetString("skin", "chosen");
            var actor = new CreationSceneObject { ModelId = model.Id, ActorSource = new() { EntityCode = "game:player", AppearanceData = skin.ToBytes() } };
            var other = new CreationSceneObject { ModelId = model.Id };
            var project = new DirectorCreationProject { Models = [model], Objects = [actor, other] };
            session.Open(project);
            var runtime = RuntimeHelpers.GetUninitializedObject(runtimeType);
            AccessTools.Field(runtimeType, "<Session>k__BackingField").SetValue(runtime, session);
            var setHand = AccessTools.Method(runtimeType, "SetActorHand");
            setHand.Invoke(runtime, [actor, 1, stack]);
            var attachment = session.Project!.Objects.Single(o => o.ParentId == actor.Id);
            Check(attachment.ParentPartId == hand.Id && !attachment.UseRecordedHandItem && attachment.ItemStackData.SequenceEqual(stack.ToBytes())
                && !session.Project.Objects.Any(o => o.ParentId == other.Id), "Equipment did not attach exclusively to the selected actor");
            session.Undo(); Check(session.Project!.Objects.Count == 2, "Undo retained added hand equipment");
            session.Redo();
            session.Save(); session.Open(session.Store.Load(project.Id));
            actor = session.Project!.Objects.Single(o => o.Id == actor.Id);
            Check(actor.ActorSource!.AppearanceData.SequenceEqual(skin.ToBytes())
                && session.Project.Objects.Single(o => o.ParentId == actor.Id).ItemStackData.SequenceEqual(stack.ToBytes()),
                "Actor appearance/equipment was lost on save and reopen");
            setHand.Invoke(runtime, [actor, 1, null]);
            attachment = session.Project.Objects.Single(o => o.ParentId == actor.Id);
            Check(attachment.ResourceCode.Length == 0 && attachment.ItemStackData.Length == 0 && !attachment.UseRecordedHandItem,
                "Clearing a hand restored recorded equipment");
            session.Undo();
            Check(session.Project!.Objects.Single(o => o.ParentId == actor.Id).ItemStackData.SequenceEqual(stack.ToBytes()),
                "Undo did not restore full item stack attributes");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Console.WriteLine("PASS actor full-stack save/reopen/undo, hand clearing and private skin atlas metadata");
    }
}
