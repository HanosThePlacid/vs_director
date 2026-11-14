using System.Numerics;
using System.Text.Json;
using VSDirector.Data;

internal static class CreationParentingChecks
{
    private static void Equal(Matrix4x4 expected, Matrix4x4 actual, string message)
    {
        foreach (var p in new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ })
            if (Vector3.Distance(Vector3.Transform(p, expected), Vector3.Transform(p, actual)) > .002f)
                throw new Exception(message);
    }

    public static void Run()
    {
        string root = Path.Combine(Path.GetTempPath(), "director-parent-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new DirectorCreationStore(root);
            var s = new DirectorCreationSession(store);
            foreach (bool animated in new[] { false, true })
            foreach (bool nonUniform in new[] { false, true })
            {
                var bone = new CreationPart { From = new(3, 16, 0), Rotation = new(12, 34, 5) };
                var model = new CreationModel { Parts = [bone] };
                var actor = new CreationSceneObject { ModelId = model.Id, Transform = new()
                    { Position = new(14, 3, -8), Rotation = new(0, 53, 0), Scale = nonUniform ? new(2, 1, .5) : new(2, 2, 2) } };
                var camera = new CreationSceneObject { Kind = CreationObjectKind.Camera, Transform = new()
                    { Position = new(19, 5, 7), Rotation = new(20, 100, 7), Pivot = new(.2, .4, .1), Scale = new(-1, 1, 1) } };
                var project = new DirectorCreationProject { Origin = new(512000, 3, 512000), Objects = [actor, camera], Models = [model], CameraId = camera.Id };
                if (animated)
                {
                    DirectorCreationEdits.SetKey(project, camera.Id, Guid.Empty, CreationChannel.Position, 0, new(19, 5, 7));
                    DirectorCreationEdits.SetKey(project, camera.Id, Guid.Empty, CreationChannel.Position, 4, new(24, 7, 9));
                    DirectorCreationEdits.SetKey(project, camera.Id, Guid.Empty, CreationChannel.Rotation, 0, new(20, 100, 7));
                    DirectorCreationEdits.SetKey(project, camera.Id, Guid.Empty, CreationChannel.Rotation, 4, new(40, 130, 10));
                }
                s.Open(project); s.Seek(2);
                var before = s.Frame!.Objects[camera.Id].World;
                string tracks = JsonSerializer.Serialize(project.Tracks, CreationJson.Options);
                var samples = Enumerable.Range(0, 17).Select(i => DirectorCreationEvaluator.Evaluate(project, i / 4d).Objects[camera.Id].World).ToArray();
                s.Reparent(camera.Id, actor.Id);
                Equal(before, s.Frame!.Objects[camera.Id].World, "Binding moved the camera");
                if (!animated && !nonUniform && camera.BindingSpace is not null) throw new Exception("Static TRS did not convert numeric fields");
                for (int i = 0; i < samples.Length; i++) Equal(samples[i], DirectorCreationEvaluator.Evaluate(project, i / 4d).Objects[camera.Id].World, "Binding changed animated interpolation");
                if (tracks != JsonSerializer.Serialize(project.Tracks, CreationJson.Options)) throw new Exception("Binding rewrote keys");
                s.Reparent(camera.Id, actor.Id, bone.Id);
                Equal(before, s.Frame!.Objects[camera.Id].World, "Bone binding moved the camera");
                s.Save();
                var loaded = store.Load(project.Id);
                Equal(before, DirectorCreationEvaluator.Evaluate(loaded, 2).Objects[camera.Id].World, "Binding did not persist");
                actor.Transform.Position += new CreationVector(6, 0, 0); s.Refresh();
                var moved = before * Matrix4x4.CreateTranslation(6, 0, 0);
                Equal(moved, s.Frame!.Objects[camera.Id].World, "Bound camera did not follow actor");
                s.Reparent(camera.Id, Guid.Empty);
                Equal(moved, s.Frame!.Objects[camera.Id].World, "Unbinding moved the camera");
                s.Undo(); Equal(moved, s.Frame!.Objects[camera.Id].World, "Parent undo moved the camera");
                if (s.Project!.Objects.Find(o => o.Id == camera.Id)!.ParentPartId != bone.Id) throw new Exception("Undo lost bone binding");
                s.Redo(); Equal(moved, s.Frame!.Objects[camera.Id].World, "Parent redo moved the camera");
            }
            // Invalid hierarchy changes must not mutate the document.
            var a = new CreationSceneObject(); var b = new CreationSceneObject();
            var p = new DirectorCreationProject { Objects = [a, b] }; s.Open(p); s.Reparent(b.Id, a.Id);
            string snapshot = JsonSerializer.Serialize(p, CreationJson.Options);
            foreach (var ids in new[] { (a.Id, b.Id), (a.Id, a.Id), (a.Id, Guid.NewGuid()) })
            {
                bool rejected = false; try { s.Reparent(ids.Item1, ids.Item2); } catch (InvalidOperationException) { rejected = true; }
                if (!rejected || snapshot != JsonSerializer.Serialize(p, CreationJson.Options)) throw new Exception("Invalid parenting mutated document");
            }
            s.Reparent(b.Id, Guid.Empty); a.Transform.Scale = new(0, 1, 1); s.Refresh();
            bool singular = false; try { s.Reparent(b.Id, a.Id); } catch (InvalidOperationException) { singular = true; }
            if (!singular || b.ParentId != Guid.Empty) throw new Exception("Singular binding not rejected");
            // An uncommitted preview must remain visible and retain its key workflow.
            a.Transform = new() { Position = new(4, 2, 1) }; s.Refresh(); s.SetEditTask(CreationEditTask.Animation); s.AutoKey = false;
            s.BeginGesture(); s.SetTransform(b.Id, Guid.Empty, new() { Position = new(8, 4, 2), Rotation = new(0, 42, 0) }); s.Changed(); s.EndGesture("draft");
            var draft = s.Frame!.Objects[b.Id].World; s.Reparent(b.Id, a.Id);
            Equal(draft, s.Frame!.Objects[b.Id].World, "Binding lost draft preview");
            s.CommitTransformDrafts(); Equal(draft, s.Frame!.Objects[b.Id].World, "Committing bound draft moved it");
            // Binding must not bake or modify shared recorded takes.
            var recordedActor = new CreationSceneObject(); var parent = new CreationSceneObject { Transform = new() { Position = new(20, 0, 8), Rotation = new(0, 70, 0) } };
            var asset = new CreationAsset { Kind = CreationAssetKind.Performance, Source = "assets/parent-take.json" };
            var motion = new CreationTrack { Channel = CreationChannel.Position };
            DirectorCreationEdits.SetKey(motion, 0, new(1, 2, 3)); DirectorCreationEdits.SetKey(motion, 4, new(10, 2, 8));
            var take = new CreationPerformance { Tracks = [motion], Action = new() { Layers = [new() { Frames = [new() { TimeSeconds = 0 }, new() { TimeSeconds = 4 }] }] } };
            var clip = new CreationClip { ObjectId = recordedActor.Id, PerformanceAssetId = asset.Id, Start = 1, Duration = 4 };
            var recorded = new DirectorCreationProject { Objects = [recordedActor, parent], Assets = [asset], Clips = [clip] }; recorded.Performances[asset.Id] = take;
            string takeBefore = JsonSerializer.Serialize(take, CreationJson.Options);
            s.Open(recorded); s.Seek(2); var times = new[] { 0d, 1, 2, 3, 4.9, 5, 6 };
            var original = times.Select(t => DirectorCreationEvaluator.Evaluate(recorded, t).Objects[recordedActor.Id].World).ToArray();
            s.Reparent(recordedActor.Id, parent.Id);
            for (int i = 0; i < times.Length; i++) Equal(original[i], DirectorCreationEvaluator.Evaluate(recorded, times[i]).Objects[recordedActor.Id].World, "Binding changed take or clip boundary");
            if (takeBefore != JsonSerializer.Serialize(take, CreationJson.Options)) throw new Exception("Binding modified shared performance");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
