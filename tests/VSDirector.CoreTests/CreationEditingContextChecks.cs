using VSDirector.Data;

internal static class CreationEditingContextChecks
{
    public static void Run()
    {
        string root=Path.Combine(Path.GetTempPath(),"director-context-"+Guid.NewGuid().ToString("N"));
        try
        {
            var bone=new CreationPart();var model=new CreationModel{Parts=[bone]};var actor=new CreationSceneObject{ModelId=model.Id};
            var project=new DirectorCreationProject{Models=[model],Objects=[actor]};var s=new DirectorCreationSession(new(root));s.Open(project);
            s.SelectedObject=actor.Id;s.Seek(2);
            s.BeginGesture();s.SetTransform(actor.Id,Guid.Empty,new(){Position=new(3,0,0)});s.Changed();s.EndGesture("place");
            if(project.Tracks.Count!=0||actor.Transform.Position.X!=3||s.HasTransformDraft)throw new Exception("Placement created keys or pending preview");
            s.SetEditTask(CreationEditTask.Animation);
            s.BeginGesture();s.SetTransform(actor.Id,Guid.Empty,new(){Position=new(5,0,0)});s.Changed();s.EndGesture("animate");
            if(project.Tracks.Single().Keys.Count!=2||project.Tracks[0].Keys.First(k=>k.Time==0).Value.X!=3)throw new Exception("First animation edit lost opening state");
            s.Undo();if(s.Project!.Tracks.Count!=0)throw new Exception("Seed and destination are not one undo");s.Redo();project=s.Project!;actor=project.Objects[0];
            s.BeginGesture();s.SetTransform(actor.Id,Guid.Empty,new(){Position=new(8,0,0)});s.Changed();s.CancelGesture();
            if(s.Frame!.Objects[actor.Id].Transform.Position.X!=5)throw new Exception("Cancelled gesture retained authored values");
            project=s.Project!;actor=project.Objects[0];s.BeginScenePose(actor.Id);
            s.BeginGesture();s.SetPosePart(bone.Id,new(){Rotation=new(0,50,0)});s.Changed();s.EndGesture("pose");
            if(s.HasPoseDraft||actor.PoseKeys.Count!=2||actor.PoseKeys.First(k=>k.Time==2).Bones[bone.Id].Rotation.Y!=50)throw new Exception("Automatic pose did not commit seed and target");
            s.Undo();if(s.Project!.Objects[0].PoseKeys.Count!=0)throw new Exception("Auto pose is not atomic");s.Redo();
            s.Seek(3);s.AutoKey=false;s.BeginGesture();s.SetPosePart(bone.Id,new(){Rotation=new(0,75,0)});s.Changed();s.EndGesture("preview");
            if(!s.HasPoseDraft||s.Project!.Objects[0].PoseKeys.Count!=2)throw new Exception("Manual pose changed source before commit");
            bool blocked=false;try{s.SetEditTask(CreationEditTask.Placement);}catch(InvalidOperationException){blocked=true;}
            if(!blocked)throw new Exception("Task switch silently discarded preview");
            s.CommitTransformDrafts();if(s.HasPoseDraft||s.Project.Objects[0].PoseKeys.Count!=3)throw new Exception("Manual pose commit failed");
            s.BeginGesture();s.SetPosePart(bone.Id,new(){Rotation=new(0,80,0)});s.Changed();s.CancelGesture();
            if(s.HasPoseDraft)throw new Exception("Cancelling a fresh manual pose created a draft");
            s.BeginGesture();s.SetPosePart(bone.Id,new(){Rotation=new(0,82,0)});s.Changed();s.EndGesture("preview");
            s.BeginGesture();s.SetPosePart(bone.Id,new(){Rotation=new(0,95,0)});s.Changed();s.CancelGesture();
            if(!s.HasPoseDraft||s.Frame!.Objects[actor.Id].Pose[bone.Id].Rotation.Y!=82)throw new Exception("Cancelling a later drag lost the existing preview");
            s.ClearTransformDrafts();
            s.SetEditTask(CreationEditTask.Placement);s.Seek(2);s.BeginGesture();s.SetTransform(actor.Id,Guid.Empty,new(){Position=new(9,0,0)});s.Changed();s.EndGesture("base");
            if(s.Project.Objects[0].Transform.Position.X!=9||s.Project.Tracks[0].Keys.First(k=>k.Time==2).Value.X!=5)throw new Exception("Base edit rewrote animation");
            s.Save();s.Store.Load(project.Id).Validate();
            var camera=new CreationSceneObject{Kind=CreationObjectKind.Camera,Transform=new(){Position=new(2,3,4)}};
            var cameraProject=new DirectorCreationProject{Origin=new(500000,100,500000),Objects=[camera],CameraId=camera.Id};
            var position=new CreationTrack{ObjectId=camera.Id,Channel=CreationChannel.Position};
            DirectorCreationEdits.SetKey(position,0,new(2,3,4));DirectorCreationEdits.SetKey(position,4,new(22,3,4));cameraProject.Tracks.Add(position);
            s.Open(cameraProject);s.Seek(2);
            var preview=DirectorCreationEvaluator.Camera(cameraProject,s.Frame!)!;
            s.RecordTransformKeys(camera.Id,Guid.Empty);
            var recorded=DirectorCreationEvaluator.Camera(cameraProject,s.Frame!)!;
            if(preview.X!=recorded.X||preview.Y!=recorded.Y||preview.Z!=recorded.Z||position.Keys.Single(k=>k.Time==2).Value.X!=12)throw new Exception("Recording preview replaced evaluated camera with its base transform");
            s.Undo();if(s.Project!.Tracks.Count!=1||s.Project.Tracks[0].Keys.Count!=2)throw new Exception("Transform keys did not undo atomically");
            s.Redo();s.SetEditTask(CreationEditTask.Animation);s.AutoKey=false;s.Seek(3);
            var before=s.Frame!.Objects[camera.Id].Transform with{};
            s.BeginGesture();s.SetTransform(camera.Id,Guid.Empty,before with{Position=new(30,4,5),Pivot=new(.5,0,0),Rotation=new(0,35,0)});s.Changed();s.EndGesture("camera draft");
            var draft=s.Frame!.Objects[camera.Id].Transform with{};s.RecordTransformKeys(camera.Id,Guid.Empty);
            if(s.HasTransformDraft||s.Frame!.Objects[camera.Id].Transform!=draft)throw new Exception("Recording preview lost draft position/rotation/pivot");
            s.Seek(0);if(s.Frame!.Objects[camera.Id].Transform.Position!=new CreationVector(2,3,4))throw new Exception("Recording preview overwrote opening camera pose");
        }
        finally{if(Directory.Exists(root))Directory.Delete(root,true);}
    }
}
