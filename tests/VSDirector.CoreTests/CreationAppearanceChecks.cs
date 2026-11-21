using VSDirector.Data;

internal static class CreationAppearanceChecks
{
    public static void Run()
    {
        var body = new CreationPart { Name = "body" };
        var arm = new CreationPart { Name = "arm", ParentId = body.Id };
        var hand = new CreationPart { Name = "RightHand", ParentId = arm.Id, Group = true, Faces = new() };
        var coat = new CreationPart { Name = "coat", ParentId = body.Id };
        var model = new CreationModel { Parts = [body, arm, hand, coat], BodyMapping = new() { ["arm"] = arm.Id } };
        var actor = new CreationSceneObject { ModelId = model.Id, Transform = new() { Position = new(3, 4, 5) } };
        actor.Pose[arm.Id] = new() { Rotation = new(10, 20, 30) };
        actor.PoseKeys.Add(new() { Time = 1, Bones = new() { [coat.Id] = new() { Rotation = new(0, 5, 0) } } });
        var other = new CreationSceneObject { ModelId = model.Id };
        var attachment = new CreationSceneObject { ParentId = actor.Id, ParentPartId = hand.Id, ActorHand = 1, Kind = CreationObjectKind.Item };
        var animation = new CreationAnimation { ModelId = model.Id };
        var track = new CreationTrack { PartId = arm.Id, Channel = CreationChannel.Rotation };
        DirectorCreationEdits.SetKey(track, 0, default);
        DirectorCreationEdits.SetKey(track, 1, new(0, 30, 0));
        animation.Tracks.Add(track);
        var clip = new CreationClip { ObjectId = actor.Id, AnimationId = animation.Id };
        var otherClip = new CreationClip { ObjectId = other.Id, AnimationId = animation.Id };
        var project = new DirectorCreationProject
        {
            Models = [model], Objects = [actor, other, attachment],
            Animations = [animation], Clips = [clip, otherClip]
        };
        var capturedBody = new CreationPart { Name = "body" };
        var capturedArm = new CreationPart { Name = "arm", ParentId = capturedBody.Id };
        var capturedHand = new CreationPart { Name = "RightHand", ParentId = capturedArm.Id, Group = true, Faces = new() };
        var shirt = new CreationPart { Name = "shirt", ParentId = capturedBody.Id };
        var captured = new CreationModel { Parts = [capturedBody, capturedArm, capturedHand, shirt] };
        var before = CreationJson.Clone(project);
        var actions = DirectorCreationAppearance.Apply(project, actor, captured);
        project.Validate();
        var updated = project.Models.First(m => m.Id == actor.ModelId);
        Check(actor.ModelId != model.Id && other.ModelId == model.Id, "Appearance changed a shared actor");
        Check(updated.Parts.Single(p => p.Name == "arm").Id == arm.Id, "Arm identity changed");
        Check(updated.Parts.Single(p => p.Name == "RightHand").Id == hand.Id && attachment.ParentPartId == hand.Id,
            "Hand attachment was disconnected");
        Check(updated.Parts.Single(p => p.Id == coat.Id).Group && updated.Parts.Single(p => p.Id == coat.Id).Faces.Count == 0,
            "Removed clothing still renders or lost its recorded references");
        Check(updated.Parts.Any(p => p.Name == "shirt" && !p.Group), "New garment was omitted");
        Check(actor.Transform.Position == new CreationVector(3, 4, 5) && actor.Pose[arm.Id].Rotation.Y == 20,
            "Appearance reset actor placement or pose");
        Check(clip.AnimationId == actions[animation.Id] && otherClip.AnimationId == animation.Id,
            "Appearance changed the wrong clip's action");
        var copied = project.Animations.First(a => a.Id == clip.AnimationId);
        Check(copied.Tracks[0].PartId == arm.Id && copied.Tracks[0].Id != track.Id
            && copied.Tracks[0].Keys[0].Id != track.Keys[0].Id, "Copied action references or identities are invalid");
        copied.Tracks[0].Keys[0].Value = new(0, 90, 0);
        Check(track.Keys[0].Value == default && model.Parts.Count == 4, "Appearance mutated shared source data");
        Check(captured.Parts[0].Id == capturedBody.Id && before.Objects[0].ModelId == model.Id,
            "Appearance mutated its capture or history snapshot");
        string root=Path.Combine(Path.GetTempPath(),"director-equipment-"+Guid.NewGuid().ToString("N"));
        var session=new DirectorCreationSession(new(root));session.Open(project);
        var unrelated=new CreationAnimation{ModelId=model.Id,Duration=2000,Tracks=[new(){Keys=Enumerable.Range(0,50000).Select(i=>new CreationKeyframe{Time=i/30d}).ToList()}]};
        project.Animations.Add(unrelated);
        int models=project.Models.Count, animations=project.Animations.Count;
        var source=new CreationActorSource{AppearanceData=[1,2,3]};
        long allocated=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<5;i++)session.ReplaceActorAppearance("Clothes",actor.Id,captured,source,[]);
        Check(GC.GetAllocatedBytesForCurrentThread()-allocated<2000000,"Clothing edits copied the unrelated library");
        Check(project.Models.Count==models&&project.Animations.Count==animations,"Repeated dressing duplicated models/actions");
        Check(actor.ModelId==updated.Id&&clip.AnimationId==copied.Id,"Private actor appearance changed animation identity");
        for(int i=0;i<5;i++)session.Undo();
        Check(actor.ActorSource is null&&project.Models.Count==models,"Scoped appearance undo lost source or models");
        for(int i=0;i<5;i++)session.Redo();
        Check(actor.ActorSource!.AppearanceData.SequenceEqual(new byte[]{1,2,3}),"Scoped appearance redo failed");
        var newHand=new CreationSceneObject{Kind=CreationObjectKind.Item,ParentId=actor.Id,ParentPartId=hand.Id,ActorHand=2};
        session.EditActorHand("Hand",actor.Id,newHand,o=>o.ResourceCode="game:torch");
        session.Undo();Check(!project.Objects.Any(o=>o.Id==newHand.Id),"Hand undo left new attachment");
        session.Redo();Check(project.Objects.Single(o=>o.Id==newHand.Id).ResourceCode=="game:torch","Hand redo failed");
        session.Edit("Placement",p=>p.Objects.First(o=>o.Id==actor.Id).Name="Renamed");
        session.Undo();session.Undo();session.Redo();session.Redo();
        Check(session.Project!.Objects.First(o=>o.Id==actor.Id).Name=="Renamed"&&session.Project.Animations.Last().Tracks[0].Keys.Count==50000,"Mixed full/equipment history lost data");
        var sharedSession=new DirectorCreationSession(new(root));sharedSession.Open(before);
        int originalModels=before.Models.Count,originalActions=before.Animations.Count;
        sharedSession.ReplaceActorAppearance("Shared clothes",actor.Id,captured,source,[]);
        Check(before.Models.Count==originalModels+1&&before.Animations.Count==originalActions+1,"Shared actor was not isolated");
        sharedSession.Undo();before.Validate();
        Check(before.Models.Count==originalModels&&before.Animations.Count==originalActions&&before.Objects[0].ModelId==model.Id,"Shared appearance undo retained copies");
        sharedSession.Redo();before.Validate();
        Check(before.Models.Count==originalModels+1&&before.Animations.Count==originalActions+1&&before.Objects[1].ModelId==model.Id,"Shared appearance redo lost isolation");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }
}
