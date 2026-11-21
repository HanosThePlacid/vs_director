namespace VSDirector.Data;

// Pose gestures never edit model geometry or the rest of the animation library.
// History owns these lists until restored, just as it owns full project snapshots.
internal sealed record CreationPoseHistoryState(Guid Actor, Guid Animation,
    List<CreationScenePoseKey>? SceneKeys, List<CreationTrack>? Tracks)
{
    internal static CreationPoseHistoryState Capture(DirectorCreationProject project, Guid actor, Guid animation)
        => animation == Guid.Empty
            ? new(actor, animation, project.Objects.First(o => o.Id == actor).PoseKeys.Select(Copy).ToList(), null)
            : new(actor, animation, null, project.Animations.First(a => a.Id == animation).Tracks.Select(t => t.Copy()).ToList());

    internal void Restore(DirectorCreationProject project)
    {
        if (SceneKeys is not null) project.Objects.First(o => o.Id == Actor).PoseKeys = SceneKeys;
        else project.Animations.First(a => a.Id == Animation).Tracks = Tracks!;
    }

    private static CreationScenePoseKey Copy(CreationScenePoseKey key) => new()
    {
        Id = key.Id, Time = key.Time, Interpolation = key.Interpolation,
        Bones = key.Bones.ToDictionary(p => p.Key, p => p.Value with { }), Hidden = new(key.Hidden)
    };
}
