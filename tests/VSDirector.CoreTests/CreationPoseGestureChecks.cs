using VSDirector.Data;

internal static class CreationPoseGestureChecks
{
    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "director-pose-gesture-" + Guid.NewGuid().ToString("N"));
        try
        {
            var part = new CreationPart();
            var model = new CreationModel { Parts = [part] };
            var actor = new CreationSceneObject { ModelId = model.Id };
            var action = new CreationAnimation { ModelId = model.Id, PoseEditing = true, Duration = 3 };
            var library = new CreationAnimation { ModelId = model.Id, Tracks = [new() {
                Keys = Enumerable.Range(0, 50000).Select(i => new CreationKeyframe { Time = i / 30d }).ToList() }] };
            var project = new DirectorCreationProject { Models = [model], Objects = [actor], Animations = [action, library] };
            var s = new DirectorCreationSession(new(root)); s.Open(project); s.BeginScenePose(actor.Id); s.Seek(1);
            void Rotate(double angle)
            {
                s.BeginGesture(poseOnly: true); s.SetPosePart(part.Id, new() { Rotation = new(0, angle, 0) });
                s.Changed(); s.EndGesture("rotate");
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread(); Rotate(25);
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            if (allocated > 1000000) throw new Exception("Pose copied unrelated animation library: " + allocated);
            if (actor.PoseKeys.Count != 2 || s.HasPoseDraft) throw new Exception("Auto pose lost seed or retained draft");
            s.Undo(); if (actor.PoseKeys.Count != 0) throw new Exception("Pose undo left keys");
            s.Redo(); if (actor.PoseKeys.Single(k => k.Time == 1).Bones[part.Id].Rotation.Y != 25) throw new Exception("Pose redo lost rotation");
            int entries = s.History.Entries.Count(); Rotate(25);
            if (s.History.Entries.Count() != entries) throw new Exception("Unchanged pose recorded undo");
            s.BeginGesture(poseOnly: true); s.SetPosePart(part.Id, new() { Rotation = new(0, 50, 0) }); s.Changed(); s.CancelGesture();
            if (s.HasPoseDraft || s.Frame!.Objects[actor.Id].Pose[part.Id].Rotation.Y != 25) throw new Exception("Cancel committed pose");
            s.AutoKey = false; Rotate(40); Rotate(60); s.Undo();
            if (s.Frame!.Objects[actor.Id].Pose[part.Id].Rotation.Y != 40) throw new Exception("Manual undo lost earlier draft");
            s.Redo(); if (s.Frame!.Objects[actor.Id].Pose[part.Id].Rotation.Y != 60) throw new Exception("Manual redo failed");
            s.SetProperty(actor.Id, Guid.Empty, CreationChannel.Color, new(.5, .6, .7), () => { }); s.Changed();
            s.BeginGesture(poseOnly: true); s.SetPosePart(part.Id, new() { Rotation = new(0, 80, 0) }); s.Changed(); s.CancelGesture();
            if (s.Frame!.Objects[actor.Id].Pose[part.Id].Rotation.Y != 60) throw new Exception("Cancel lost existing draft");
            if (s.Frame.Objects[actor.Id].Color != new CreationVector(.5, .6, .7)) throw new Exception("Pose cancel lost another property preview");
            s.ClearTransformDrafts(); s.AutoKey = true; s.EnterAnimation(action.Id, actor.Id); s.Seek(1);
            allocated = GC.GetAllocatedBytesForCurrentThread(); Rotate(75);
            if (GC.GetAllocatedBytesForCurrentThread() - allocated > 1000000) throw new Exception("Action pose copied unrelated library");
            if (action.Tracks.Count != 6 || action.Tracks.Any(t => t.Keys.Count != 2)) throw new Exception("Action gesture lost seed/channels");
            s.Undo(); if (action.Tracks.Count != 0) throw new Exception("Action undo left channels");
            s.Redo(); if (s.Frame!.Objects[actor.Id].Pose[part.Id].Rotation.Y != 75) throw new Exception("Action redo lost pose");
            s.Edit("geometry", p => p.Models[0].Parts[0].To = new(5, 6, 7));
            Rotate(90); s.Undo(); s.Undo();
            if (s.Project!.Models[0].Parts[0].To == new CreationVector(5, 6, 7)) throw new Exception("Mixed undo retained geometry edit");
            s.Redo(); s.Redo();
            if (s.Project.Models[0].Parts[0].To != new CreationVector(5, 6, 7)
                || s.Frame!.Objects[actor.Id].Pose[part.Id].Rotation.Y != 90) throw new Exception("Mixed redo corrupted geometry/pose");
            s.Undo(); Rotate(100);
            if (s.History.CanRedo) throw new Exception("Pose branch retained redo");
            if (s.Project.Animations[1].Tracks[0].Keys.Count != 50000) throw new Exception("Pose changed imported actions");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
