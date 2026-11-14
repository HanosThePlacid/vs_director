namespace VSDirector.Data;

// User preferences, never a part of an existing project's saved state.
public sealed class CreationProjectDefaults
{
    public double Duration { get; set; } = 10;
    public CreationTimeDisplay TimeDisplay { get; set; } = CreationTimeDisplay.Frames;
    public bool Loop { get; set; }
    public CreationOutput Output { get; set; } = new();
    public bool ExportTextures { get; set; } = true;
    public void Normalize()
    {
        Duration=double.IsFinite(Duration)?Math.Clamp(Duration,DirectorCreationTiming.FrameDuration,86400):10;
        if(!Enum.IsDefined(TimeDisplay))TimeDisplay=CreationTimeDisplay.Frames;
        Output??=new();Output.Width=Math.Clamp(Output.Width,16,8192)/2*2;Output.Height=Math.Clamp(Output.Height,16,8192)/2*2;
        Output.Fps=Math.Clamp(Output.Fps,1,240);Output.Quality=Math.Clamp(Output.Quality,0,51);
        Output.Encoder??="Auto";Output.Directory??="";
    }
    public void Apply(DirectorCreationProject project)
    {
        Normalize();project.Duration=Duration;project.LoopStart=0;project.LoopEnd=Duration;
        project.Loop=Loop;project.TimeDisplay=TimeDisplay;project.Output=CreationJson.Clone(Output);
    }
}

public sealed class CreationOverlayPreferences
{
    public bool ObjectPoints { get; set; }
    public bool EmptyHands { get; set; }
    public bool BoneLines { get; set; }
    public bool PoseHandles { get; set; } = true;
    public bool IkTargets { get; set; } = true;
    public bool IkPoles { get; set; } = true;
    public bool IkRelations { get; set; } = true;
    public bool IkTips { get; set; }
    public bool AllChains { get; set; }
    public float Size { get; set; } = 7;
    public float Opacity { get; set; } = .8f;
    public uint TargetColor { get; set; } = 0xffffffff;
    public uint PoleColor { get; set; } = 0xff268cff;
    public uint BoneColor { get; set; } = 0xffe8c685;
    public void Normalize()
    {
        Size=float.IsFinite(Size)?Math.Clamp(Size,4,16):7;
        Opacity=float.IsFinite(Opacity)?Math.Clamp(Opacity,.1f,1):.8f;
    }
}

public static class DirectorCreationAttachments
{
    // Preserve authored and recorded attachments, including items that appear
    // later in a take. Only plain, unused placeholders are hidden.
    public static bool HasContent(DirectorCreationProject project,CreationSceneObject obj)
    {
        if(obj.ActorHand is not (1 or 2))return true;
        if(obj.ItemStackData.Length>0 || obj.ResourceCode.Length>0 || obj.Transform!=new CreationTransform()
            || !obj.Name.Equals(obj.ActorHand==1?"RightHand":"LeftHand",StringComparison.OrdinalIgnoreCase)
            || obj.Kind!=CreationObjectKind.Item || obj.ModelId!=Guid.Empty || obj.AssetId!=Guid.Empty
            || obj.Pose.Count>0 || obj.HiddenParts.Count>0 || obj.IkChains.Count>0
            || !obj.Visible || obj.Color!=CreationVector.One || obj.Opacity!=1 || obj.Glow!=0
            || obj.PoseKeys.Count>0 || project.Objects.Any(o=>o.ParentId==obj.Id)
            || project.Tracks.Any(t=>t.ObjectId==obj.Id&&t.Keys.Count>0)
            || project.Animations.Any(a=>a.Tracks.Any(t=>t.ObjectId==obj.Id&&t.Keys.Count>0))
            || project.Clips.Any(c=>c.ObjectId==obj.Id))return true;
        if(!obj.UseRecordedHandItem)return false;
        foreach(var clip in project.Clips.Where(c=>c.ObjectId==obj.ParentId&&c.PerformanceAssetId!=Guid.Empty))
        {
            if(!project.Performances.TryGetValue(clip.PerformanceAssetId,out var take))return true;
            if(take.Channels.HasFlag(CreationRecordingChannels.Equipment) && take.Action.Layers.SelectMany(l=>l.Frames)
                .Any(f=>(obj.ActorHand==1?f.State.RightHandItem:f.State.LeftHandItem)?.Length>0))return true;
        }
        return false;
    }
}
