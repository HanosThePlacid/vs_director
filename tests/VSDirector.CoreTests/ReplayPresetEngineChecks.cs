using VSDirector.Data;

/// <summary>Engine pieces the replay preset library relies on.</summary>
internal static class ReplayPresetEngineChecks
{
    public static void Run()
    {
        EndsElementReleasesTrack();
        TimeEffectsMapContentTime();
        FollowBlendAnimatesRig();
        LookAtAimsAndFrames();
        UsagePersists();
    }

    private static void EndsElementReleasesTrack()
    {
        var frames = new List<DirectorReplayElementKeyframe>
        {
            new() { TimeSeconds = 1d, Fov = 40f, TrackType = DirectorReplayElementTrackType.Fov,
                InterpolationToNext = DirectorReplayElementInterpolation.Linear },
            new() { TimeSeconds = 3d, Fov = 80f, TrackType = DirectorReplayElementTrackType.Fov,
                InterpolationToNext = DirectorReplayElementInterpolation.Hold, EndsElement = true }
        };
        Check.True(DirectorReplayElementTimeline.TrySample(frames, DirectorReplayElementTrackType.Fov, 2d, out var middle));
        Check.NearlyEqual(60d, middle.Fov, 1e-4);
        Check.False(DirectorReplayElementTimeline.TrySample(frames, DirectorReplayElementTrackType.Fov, 3d, out _));
        Check.False(DirectorReplayElementTimeline.TrySample(frames, DirectorReplayElementTrackType.Fov, 10d, out _));
        frames[1].EndsElement = false;
        Check.True(DirectorReplayElementTimeline.TrySample(frames, DirectorReplayElementTrackType.Fov, 10d, out var held));
        Check.NearlyEqual(80d, held.Fov, 1e-4);

        // A clone keeps the flag (presets copy keyframes to build end markers).
        Check.True(new DirectorReplayElementKeyframe { EndsElement = true }.DeepClone().EndsElement);
    }

    private static DirectorReplayViewportTimeline FreezeTimeline(DirectorReplayTimeEffect effect, double seconds, double rate)
    {
        var timeline = new DirectorReplayViewportTimeline();
        var track = new DirectorReplayElementTrack { TrackType = DirectorReplayElementTrackType.Freeze };
        timeline.Tracks.Add(track);
        timeline.ElementKeyframes.Add(new DirectorReplayElementKeyframe
        {
            TrackId = track.Id, TrackType = DirectorReplayElementTrackType.Freeze, TimeSeconds = 10d, Frozen = true,
            TimeEffect = effect, TimeEffectSeconds = seconds, TimeEffectRate = rate,
            InterpolationToNext = DirectorReplayElementInterpolation.Hold
        });
        timeline.ElementKeyframes.Add(new DirectorReplayElementKeyframe
        {
            TrackId = track.Id, TrackType = DirectorReplayElementTrackType.Freeze, TimeSeconds = 15d, Frozen = false,
            InterpolationToNext = DirectorReplayElementInterpolation.Hold
        });
        return timeline;
    }

    private static void TimeEffectsMapContentTime()
    {
        DirectorReplayViewportTimeline rewind = FreezeTimeline(DirectorReplayTimeEffect.Rewind, 3d, 1d);
        var start = DirectorReplayElementTimeline.SampleFreeze(rewind, 10d);
        Check.NearlyEqual(10d, start.ReplayTimeSeconds, 1e-6);
        var back = DirectorReplayElementTimeline.SampleFreeze(rewind, 11d);
        Check.NearlyEqual(7d, back.ReplayTimeSeconds, 1e-6);
        Check.NearlyEqual(11d, back.CameraTimeSeconds ?? double.NaN, 1e-6);
        var replaying = DirectorReplayElementTimeline.SampleFreeze(rewind, 12.5d);
        Check.NearlyEqual(8.5d, replaying.ReplayTimeSeconds, 1e-6);
        Check.True(DirectorReplayElementTimeline.SampleFreeze(rewind, 10.5d).RewindAmount > 0.5d);
        Check.NearlyEqual(16d, DirectorReplayElementTimeline.SampleFreeze(rewind, 16d).ReplayTimeSeconds, 1e-6);

        DirectorReplayViewportTimeline stutter = FreezeTimeline(DirectorReplayTimeEffect.Stutter, 3d, 4d);
        Check.NearlyEqual(10d, DirectorReplayElementTimeline.SampleFreeze(stutter, 10.2d).ReplayTimeSeconds, 1e-6);
        Check.NearlyEqual(10.25d, DirectorReplayElementTimeline.SampleFreeze(stutter, 10.3d).ReplayTimeSeconds, 1e-6);

        DirectorReplayViewportTimeline slow = FreezeTimeline(DirectorReplayTimeEffect.Slow, 3d, 0.25d);
        var slowed = DirectorReplayElementTimeline.SampleFreeze(slow, 12d);
        Check.NearlyEqual(10.5d, slowed.ReplayTimeSeconds, 1e-6);
        Check.NearlyEqual(12d, slowed.CameraTimeSeconds ?? double.NaN, 1e-6);
    }

    private static void FollowBlendAnimatesRig()
    {
        var start = new DirectorReplayElementKeyframe
        {
            TimeSeconds = 0d, FollowDistance = 3d, FollowHeight = 0.5d, Roll = 0f,
            FollowBlend = DirectorReplayElementInterpolation.Linear
        };
        var end = new DirectorReplayElementKeyframe { TimeSeconds = 4d, FollowDistance = 11d, FollowHeight = 8.5d, Roll = 20f };
        DirectorReplayElementKeyframe half = DirectorReplayFollowBlend.Sample(start, end, 2d);
        Check.NearlyEqual(7d, half.FollowDistance);
        Check.NearlyEqual(4.5d, half.FollowHeight);
        Check.NearlyEqual(10d, half.Roll, 1e-4);
        start.FollowBlend = DirectorReplayElementInterpolation.Hold;
        Check.NearlyEqual(3d, DirectorReplayFollowBlend.Sample(start, end, 2d).FollowDistance);
    }

    private static void LookAtAimsAndFrames()
    {
        var tripod = new DirectorReplayElementKeyframe
        {
            TrackMode = DirectorReplayTrackMode.Tripod, X = 0d, Y = 0d, Z = 10d,
            TrackAutoFrame = true, TrackFrameSize = 0.5d
        };
        var subject = new DirectorReplayCameraElementMath.LookSubject(0d, 0d, 0d, 1d);
        CameraPreset pose = DirectorReplayCameraElementMath.EvaluateLookAt(tripod, subject, null, 0, 70f);
        Check.NearlyEqual(0d, pose.Yaw, 1e-4);
        Check.NearlyEqual(0d, pose.Pitch, 1e-4);
        double expectedFov = 2d * Math.Atan(1d / (10d * 0.5d)) * 180d / Math.PI;
        Check.NearlyEqual(expectedFov, pose.Fov, 1e-3);

        tripod.TrackAutoFrame = false;
        tripod.Y = 10d;
        CameraPreset high = DirectorReplayCameraElementMath.EvaluateLookAt(tripod, subject, null, 0, 70f);
        Check.NearlyEqual(-45d, high.Pitch, 1e-3);
        Check.NearlyEqual(70d, high.Fov, 1e-4);
    }

    private static void UsagePersists()
    {
        string directory = Path.Combine(Path.GetTempPath(), "vsdirector-usage-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "usage.json");
        try
        {
            DirectorReplayPresetUsage usage = DirectorReplayPresetUsage.Load(path);
            usage.RecordUse("a");
            usage.RecordUse("b");
            usage.RecordUse("a");
            usage.ToggleFavorite("b");
            usage.SetLastCategory("shake");
            DirectorReplayPresetUsage loaded = DirectorReplayPresetUsage.Load(path);
            Check.Equal("a", loaded.Recent[0]);
            Check.Equal(2, loaded.Recent.Count);
            Check.Equal(2, loaded.Count("a"));
            Check.Equal("a", loaded.MostUsed().First());
            Check.True(loaded.IsFavorite("b"));
            Check.Equal("shake", loaded.LastCategory);
            for (int i = 0; i < 40; i++) loaded.RecordUse("p" + i);
            Check.Equal(DirectorReplayPresetUsage.MaxRecent, DirectorReplayPresetUsage.Load(path).Recent.Count);
            File.WriteAllText(path, "{ not json");
            Check.Equal(0, DirectorReplayPresetUsage.Load(path).Recent.Count);
        }
        finally
        {
            try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }
}
