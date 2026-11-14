using VSDirector.Data;

internal static class CreationPreferencesChecks
{
    internal static void Run()
    {
        static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        var defaults=new CreationProjectDefaults{Duration=24,Loop=true,TimeDisplay=CreationTimeDisplay.Time,Output=new(){Width=1280,Height=720,Fps=60}};
        var old=new DirectorCreationProject();var project=new DirectorCreationProject();defaults.Apply(project);
        defaults.Output.Width=1920;
        Check(project.Output.Width==1280&&project.Output.Fps==60&&project.Duration==24&&project.LoopEnd==24&&project.Loop,"Defaults not independently applied");
        Check(old.Duration==10&&old.Output.Fps==30&&DirectorCreationTiming.FramesPerSecond==30,"Defaults altered old work or timeline rate");
        defaults.Duration=double.NaN;defaults.Output.Height=-4;defaults.Normalize();
        Check(defaults.Duration==10&&defaults.Output.Height==16,"Invalid preferences were not normalized");
        var overlay=new CreationOverlayPreferences();Check(!overlay.EmptyHands&&!overlay.BoneLines&&overlay.IkTargets&&overlay.IkPoles&&!overlay.AllChains,"Overlay defaults are not minimal");
        var actor=new CreationSceneObject();var hand=new CreationSceneObject{Name="RightHand",ActorHand=1,ParentId=actor.Id,Kind=CreationObjectKind.Item,UseRecordedHandItem=true};
        project=new(){Objects=[actor,hand]};
        Check(!DirectorCreationAttachments.HasContent(project,hand),"Empty hand placeholder exposed");
        hand.Name="Prop anchor";Check(DirectorCreationAttachments.HasContent(project,hand),"Renamed attachment hidden");hand.Name="RightHand";
        hand.Visible=false;Check(DirectorCreationAttachments.HasContent(project,hand),"Authored visibility hidden");hand.Visible=true;
        hand.Pose[Guid.NewGuid()]=new();Check(DirectorCreationAttachments.HasContent(project,hand),"Authored pose hidden");hand.Pose.Clear();
        hand.ItemStackData=[1];Check(DirectorCreationAttachments.HasContent(project,hand),"Equipped hand hidden");hand.ItemStackData=[];
        hand.Transform=new(){Position=new(1,0,0)};Check(DirectorCreationAttachments.HasContent(project,hand),"Authored attachment offset hidden");hand.Transform=new();
        var child=new CreationSceneObject{ParentId=hand.Id};project.Objects.Add(child);Check(DirectorCreationAttachments.HasContent(project,hand),"Attachment with child hidden");project.Objects.Remove(child);
        var clip=new CreationClip{ObjectId=actor.Id,PerformanceAssetId=Guid.NewGuid()};project.Clips.Add(clip);
        Check(DirectorCreationAttachments.HasContent(project,hand),"Unloaded recorded hand data hidden");
        var take=new CreationPerformance();project.Performances[clip.PerformanceAssetId]=take;
        Check(!DirectorCreationAttachments.HasContent(project,hand),"Empty recording exposed unused hand");
        take.Action.Layers.Add(new(){Frames=[new(){TimeSeconds=5,State=new(){RightHandItem=[1]}}]});
        Check(DirectorCreationAttachments.HasContent(project,hand),"Item that appears later in recording hidden");
        hand.UseRecordedHandItem=false;Check(!DirectorCreationAttachments.HasContent(project,hand),"Explicit empty hand reverted to recorded equipment");
        var track=new CreationTrack{ObjectId=hand.Id,Keys=[new()]};project.Tracks.Add(track);
        Check(DirectorCreationAttachments.HasContent(project,hand),"Keyed hand attachment hidden");
        string serialized=System.Text.Json.JsonSerializer.Serialize(project,CreationJson.Options);
        DirectorCreationAttachments.HasContent(project,hand);
        Check(serialized==System.Text.Json.JsonSerializer.Serialize(project,CreationJson.Options),"Visibility changed authored data");
    }
}
