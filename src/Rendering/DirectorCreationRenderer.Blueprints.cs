using System.Numerics;
using VSDirector.Blueprints;
using VSDirector.Data;

namespace VSDirector.Rendering;

internal sealed partial class DirectorCreationRenderer
{
    internal DirectorBlueprintService? Blueprints;
    internal readonly Dictionary<Guid,DirectorProjectionRenderFrame> ProjectionFrames=[];
    private void RenderProjection(DirectorCreationProject project,CreationObjectState state)
    {
        if(Blueprints is null)return;
        var build=Blueprints.Get(project,state.Source);
        if(!build.Ready)return;
        IReadOnlyList<BlueprintMeshPart> parts=build.Parts;
        if(state.Source.Projection!.Edited)
        {if(!ProjectionFrames.TryGetValue(state.Source.Id,out var frame))return;parts=frame.Parts;}
        shader!.Uniform("textured",1);
        shader.Uniform("vertexColorMode",2);
        shader.UniformMatrix("modelMatrix",Array(state.World));
        foreach(var part in parts.OrderBy(p=>p.Transparent))
        {
            part.Gpu??=api.Render.UploadMultiTextureMesh(part.Mesh);
            game.Platform.GlDepthMask(!part.Transparent && state.Source.Opacity>=.999);
            api.Render.RenderMultiTextureMesh(part.Gpu,"tex");
        }
        shader.Uniform("vertexColorMode",0);
    }
}
