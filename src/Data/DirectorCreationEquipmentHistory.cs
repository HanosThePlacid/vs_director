namespace VSDirector.Data;

// Equipment edits own only the replaced appearance and attachments. The action
// library, recorded performances and other actors never need a full snapshot.
internal sealed class CreationEquipmentHistoryState
{
    internal Guid Actor;
    internal Guid Model;
    internal CreationActorSource? Source;
    internal Dictionary<Guid, CreationModel?> Models = new();
    internal Dictionary<Guid, CreationAnimation?> Animations = new();
    internal Dictionary<Guid, CreationAsset?> Assets = new();
    internal Dictionary<Guid, CreationSceneObject?> Objects = new();
    internal Dictionary<Guid, Guid> Clips = new();
    internal static CreationEquipmentHistoryState CaptureActor(DirectorCreationProject p, Guid actor)
    {
        var obj = p.Objects.First(o => o.Id == actor);
        return new() { Actor = actor, Model = obj.ModelId, Source = obj.ActorSource is null ? null : CreationJson.Clone(obj.ActorSource),
            Clips = p.Clips.Where(c => c.ObjectId == actor).ToDictionary(c => c.Id, c => c.AnimationId) };
    }
    internal CreationEquipmentHistoryState CaptureCurrent(DirectorCreationProject p)
    {
        var current = CaptureActor(p, Actor);
        foreach (var id in Models.Keys) current.Models[id] = Copy(p.Models.Find(m => m.Id == id));
        foreach (var id in Animations.Keys) current.Animations[id] = Copy(p.Animations.Find(a => a.Id == id));
        foreach (var id in Assets.Keys) current.Assets[id] = Copy(p.Assets.Find(a => a.Id == id));
        foreach (var id in Objects.Keys) current.Objects[id] = Copy(p.Objects.Find(o => o.Id == id));
        return current;
    }
    private static T? Copy<T>(T? value) where T : class => value is null ? null : CreationJson.Clone(value);
    internal void Restore(DirectorCreationProject p)
    {
        var actor = p.Objects.First(o => o.Id == Actor); actor.ModelId = Model; actor.ActorSource = Source;
        RestoreList(p.Models, Models, m => m.Id); RestoreList(p.Animations, Animations, a => a.Id);
        RestoreList(p.Assets, Assets, a => a.Id); RestoreList(p.Objects, Objects, o => o.Id);
        foreach (var clip in p.Clips) if (Clips.TryGetValue(clip.Id, out var animation)) clip.AnimationId = animation;
    }
    private static void RestoreList<T>(List<T> list, Dictionary<Guid, T?> entries, Func<T, Guid> id) where T : class
    {
        foreach (var pair in entries)
        {
            int index = list.FindIndex(item => id(item) == pair.Key);
            if (pair.Value is null) { if (index >= 0) list.RemoveAt(index); }
            else if (index >= 0) list[index] = pair.Value;
            else list.Add(pair.Value);
        }
    }
}

public sealed partial class DirectorCreationSession
{
    public Dictionary<Guid, Guid> ReplaceActorAppearance(string label, Guid actorId, CreationModel model, CreationActorSource? source, List<CreationAsset> assets)
    {
        EndGesture(label);
        var p = Project ?? throw new InvalidOperationException("No project.");
        var actor = p.Objects.First(o => o.Id == actorId);
        var before = CreationEquipmentHistoryState.CaptureActor(p, actorId);
        before.Models[actor.ModelId] = CreationJson.Clone(p.Models.First(m => m.Id == actor.ModelId));
        int modelCount = p.Models.Count, animationCount = p.Animations.Count;
        try
        {
            var actions = DirectorCreationAppearance.Apply(p, actor, model);
            actor.ActorSource = source;
            foreach (var asset in assets) { before.Assets[asset.Id] = null; p.Assets.Add(asset); }
            foreach (var added in p.Models.Skip(modelCount)) before.Models[added.Id] = null;
            foreach (var added in p.Animations.Skip(animationCount)) before.Animations[added.Id] = null;
            History.RecordEquipment(before, label); Changed(); return actions;
        }
        catch
        {
            p.Models.RemoveRange(modelCount, Math.Max(0, p.Models.Count - modelCount));
            p.Animations.RemoveRange(animationCount, Math.Max(0, p.Animations.Count - animationCount));
            before.Restore(p); Refresh(); throw;
        }
    }
    public void EditActorHand(string label, Guid actorId, CreationSceneObject attachment, Action<CreationSceneObject> change)
    {
        EndGesture(label);
        var p = Project ?? throw new InvalidOperationException("No project.");
        var before = CreationEquipmentHistoryState.CaptureActor(p, actorId);
        var existing = p.Objects.Find(o => o.Id == attachment.Id);
        before.Objects[attachment.Id] = existing is null ? null : CreationJson.Clone(existing);
        try
        {
            if (existing is null) p.Objects.Add(attachment);
            change(existing ?? attachment); History.RecordEquipment(before, label); Changed();
        }
        catch { before.Restore(p); Refresh(); throw; }
    }
}
