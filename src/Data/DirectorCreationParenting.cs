using System.Numerics;

namespace VSDirector.Data;

// An affine binding basis keeps authored animation curves in their original
// space. It also preserves shear introduced by rotated, non-uniform parents.
public sealed record CreationBindingSpace
{
    public CreationVector X { get; init; } = new(1, 0, 0);
    public CreationVector Y { get; init; } = new(0, 1, 0);
    public CreationVector Z { get; init; } = new(0, 0, 1);
    public CreationVector Position { get; init; }
    public Matrix4x4 Matrix() => new(
        (float)X.X, (float)X.Y, (float)X.Z, 0,
        (float)Y.X, (float)Y.Y, (float)Y.Z, 0,
        (float)Z.X, (float)Z.Y, (float)Z.Z, 0,
        (float)Position.X, (float)Position.Y, (float)Position.Z, 1);
    public static CreationBindingSpace FromMatrix(Matrix4x4 m) => new()
    {
        X = new(m.M11, m.M12, m.M13), Y = new(m.M21, m.M22, m.M23),
        Z = new(m.M31, m.M32, m.M33), Position = CreationVector.From(m.Translation)
    };
    public bool Valid() => X.Finite() && Y.Finite() && Z.Finite() && Position.Finite()
        && float.IsFinite(Matrix().GetDeterminant());
}

public static class DirectorCreationParenting
{
    public static Matrix4x4 Attachment(CreationFrame frame, Guid parentId, Guid partId)
    {
        if (!frame.Objects.TryGetValue(parentId, out var parent)) return Matrix4x4.Identity;
        return parent.Bones.GetValueOrDefault(partId, Matrix4x4.Identity) * parent.World;
    }

    public static Matrix4x4 Space(CreationFrame frame, CreationSceneObject obj)
        => (obj.BindingSpace?.Matrix() ?? Matrix4x4.Identity) * Attachment(frame, obj.ParentId, obj.ParentPartId);
}

public sealed partial class DirectorCreationSession
{
    public void Reparent(Guid objectId, Guid parentId, Guid partId = default)
    {
        if (Project is not { } project || Frame is not { } frame
            || project.Objects.Find(o => o.Id == objectId) is not { } obj) return;
        if (parentId == Guid.Empty) partId = Guid.Empty;
        if (obj.ParentId == parentId && obj.ParentPartId == partId) return;
        if (parentId != Guid.Empty && !frame.Objects.ContainsKey(parentId))
            throw new InvalidOperationException("Parent object does not exist.");
        var visited = new HashSet<Guid>();
        for (var id = parentId; id != Guid.Empty; id = project.Objects.Find(o => o.Id == id)?.ParentId ?? Guid.Empty)
            if (id == objectId || !visited.Add(id)) throw new InvalidOperationException("Parenting would create a cycle.");
        if (partId != Guid.Empty && !frame.Objects[parentId].Bones.ContainsKey(partId))
            throw new InvalidOperationException("Attachment bone does not exist.");
        var parent = DirectorCreationParenting.Attachment(frame, parentId, partId);
        if (!Matrix4x4.Invert(parent, out var inverse))
            throw new InvalidOperationException("Parent scale is zero; cannot preserve the world transform.");
        var binding = CreationBindingSpace.FromMatrix(DirectorCreationParenting.Space(frame, obj) * inverse);
        if (!binding.Valid()) throw new InvalidOperationException("Invalid parent transform.");
        // Static objects can expose ordinary parent-local numeric fields. Keep
        // the basis for animation/drafts, or affine shear that TRS cannot express.
        CreationTransform? converted = null;
        static bool TransformTrack(CreationTrack t) => t.PartId == Guid.Empty && t.Keys.Count > 0
            && t.Channel is CreationChannel.Position or CreationChannel.Rotation or CreationChannel.Scale or CreationChannel.Pivot;
        bool authoredMotion = project.Tracks.Any(t => t.ObjectId == objectId && TransformTrack(t))
            || project.Animations.Any(a => a.Tracks.Any(t => t.ObjectId == objectId && TransformTrack(t)))
            || project.Clips.Any(c => c.ObjectId == objectId && (c.PerformanceAssetId != Guid.Empty
                || project.Animations.Any(a => a.Id == c.AnimationId && a.Tracks.Any(TransformTrack))));
        if (!HasTransformDraft && EditingAnimation is null && !authoredMotion)
        {
            var expected = obj.Transform.Matrix() * binding.Matrix();
            try
            {
                var tr = DirectorCreationMath.FromMatrix(expected, obj.Transform.Rotation, obj.Transform.Scale);
                tr.Pivot = obj.Transform.Pivot;
                var linear = expected; linear.Translation = Vector3.Zero;
                tr.Position -= tr.Pivot - CreationVector.From(Vector3.TransformNormal(tr.Pivot.Float(), linear));
                if (new[] { Vector3.Zero, Vector3.UnitX, Vector3.UnitY, Vector3.UnitZ }.All(p =>
                    Vector3.Distance(Vector3.Transform(p, expected), Vector3.Transform(p, tr.Matrix())) < .0002f)) converted = tr;
            }
            catch (InvalidDataException) { /* Preserve the full affine basis instead. */ }
        }
        Play(false);
        Edit("Change parent", _ =>
        {
            obj.ParentId = parentId; obj.ParentPartId = partId;
            obj.BindingSpace = converted is null ? binding : null;
            if (converted is not null) obj.Transform = converted;
        });
    }
}
