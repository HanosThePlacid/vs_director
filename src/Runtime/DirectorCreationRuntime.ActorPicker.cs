using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Util;
using Vintagestory.API.MathTools;
using Vintagestory.API.Common.Entities;

namespace VSDirector.Runtime;

internal sealed partial class DirectorCreationRuntime
{
    // Read geometry and textures without spawning an entity or adding anything to the work.
    internal MeshData ActorPickerMesh(string code, long entityId)
    {
        var entity = entityId == 0 ? null : api.World.GetEntityById(entityId);
        // The player catalogue entry represents the current skin/clothing, not the unskinned base mesh.
        if (entity is null && api.World.Player.Entity.Code?.ToString() == code) entity = api.World.Player.Entity;
        var properties = entity?.Properties ?? api.World.GetEntityType(new AssetLocation(code));
        var composite = (entity is null ? properties?.Client?.Shape : properties?.Client?.ShapeForEntity ?? properties?.Client?.Shape)
            ?? throw new InvalidOperationException("Actor shape is unavailable.");
        var location = composite.Base.Clone();
        location.Path = "shapes/" + location.Path + ".json";
        var shape = (properties!.Client.LoadedShapeForEntity ?? properties.Client.LoadedShape)?.Clone() ?? JsonUtil.FromString<Shape>(api.Assets.Get(location).ToText())
            ?? throw new InvalidOperationException("Actor shape is unavailable.");
        // Raw shapes still contain named faces. The tesselator only reads FacesResolved.
        shape.ResolveReferences(api.Logger, location.ToString());
        var animator = entity is EntityPlayer player ? player.TpAnimManager?.Animator ?? player.AnimManager?.Animator : entity?.AnimManager?.Animator;
        if (animator is AnimatorBase { RootElements.Length: > 0 } posed)
            shape.Elements = posed.RootElements.Select(e => e.Clone()).ToArray();
        ITexPositionSource textures;
        if (entity is not null) textures = entity.GetTextureSource();
        else textures = new ActorPickerTextureSource(api.EntityTextureAtlas, properties.Client.Textures);
        api.Tesselator.TesselateShape(new TesselationMetaData
        {
            TexSource = textures, TypeForLogging = "director-actor-picker",
            QuantityElements = composite.QuantityElements, SelectiveElements = composite.SelectiveElements,
            IgnoreElements = composite.IgnoreElements
        }, shape, out var mesh);
        mesh.Rotate(new Vec3f(.5f, 0, .5f), composite.rotateX * GameMath.DEG2RAD, composite.rotateY * GameMath.DEG2RAD, composite.rotateZ * GameMath.DEG2RAD);
        if (mesh.VerticesCount == 0) throw new InvalidOperationException("Actor shape is empty.");
        return mesh;
    }

    // Native entity textures are already baked into the entity atlas. ShapeTextureSource instead
    // uses the block atlas and does not implement the entity's "all" texture fallback.
    private sealed class ActorPickerTextureSource : ITexPositionSource
    {
        private readonly ITextureAtlasAPI atlas;
        private readonly Dictionary<string, TextureAtlasPosition> positions = new(StringComparer.Ordinal);
        public Size2i AtlasSize => atlas.Size;
        public TextureAtlasPosition this[string textureCode] =>
            textureCode is not null && (positions.TryGetValue(textureCode, out var position) || positions.TryGetValue("all", out position))
                ? position : atlas.UnknownTexturePosition;

        internal ActorPickerTextureSource(ITextureAtlasAPI atlas, IDictionary<string, CompositeTexture> textures)
        {
            this.atlas = atlas;
            foreach (var (code, texture) in textures)
            {
                var baked = texture.Baked;
                if (baked?.BakedVariants is { Length: > 0 } variants) baked = variants[0];
                if (baked is not null && baked.TextureSubId >= 0 && baked.TextureSubId < atlas.Positions.Length
                    && atlas.Positions[baked.TextureSubId] is { } position)
                    positions[code] = position;
                else if (atlas.GetOrInsertTexture(texture.Clone(), out _, out var inserted))
                    positions[code] = inserted;
            }
        }
    }
}
