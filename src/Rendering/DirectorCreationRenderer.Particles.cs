using System.Numerics;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VSDirector.Data;

namespace VSDirector.Rendering;

internal sealed partial class DirectorCreationRenderer
{
    private MeshRef? dynamicParticles;
    private int particleVertexCapacity,particleIndexCapacity;
    private void DrawParticleMesh(MeshData data)
    {
        if(dynamicParticles is null||data.VerticesCount>particleVertexCapacity||data.IndicesCount>particleIndexCapacity)
        {
            dynamicParticles?.Dispose();particleVertexCapacity=(int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(64,data.VerticesCount));
            particleIndexCapacity=(int)BitOperations.RoundUpToPowerOf2((uint)Math.Max(96,data.IndicesCount));
            var lights=new CustomMeshDataPartFloat{Values=new float[particleVertexCapacity*3],Count=particleVertexCapacity*3,InterleaveSizes=[3],InterleaveStride=12,InterleaveOffsets=[0],StaticDraw=false};
            dynamicParticles=api.Render.AllocateEmptyMesh(particleVertexCapacity*12,0,particleVertexCapacity*8,particleVertexCapacity*4,0,particleIndexCapacity*4,lights,null!,null!,null!,staticDraw:false);
        }
        api.Render.UpdateMesh(dynamicParticles,data);game.Platform.RenderMesh(dynamicParticles);
    }
    private void RenderAdvancedParticles(IReadOnlyDictionary<Guid,CreationObjectState> states)
    {
        var particles=AdvancedParticles.Where(p=>states.TryGetValue(p.ObjectId,out var state)&&state.Visible).Select(p=>
        {
            var state=states[p.ObjectId];
            return p.LegacyProgress>=0 && state.Source.ParticleEffect?.AllNodes().FirstOrDefault(e=>e.Id==p.EmitterId)?.Legacy is {} legacy
                ?p with {Color=state.Color*(1-p.LegacyProgress)+(legacy.ParticleSettings??CreationParticleSettings.Preset(legacy.ResourceCode)).EndColor*p.LegacyProgress}:p;
        }).ToArray();
        if(particles.Length==0)return;
        var view=game.PerspectiveViewMat;
        Matrix4x4.Invert(new((float)view[0],(float)view[1],(float)view[2],0,(float)view[4],(float)view[5],(float)view[6],0,
            (float)view[8],(float)view[9],(float)view[10],0,0,0,0,1),out var facing);
        shader!.Uniform("vertexColorMode",1);
        shader.Uniform("tint",1f,1f,1f,1f);
        shader.UniformMatrix("modelMatrix",Array(Matrix4x4.Identity));
        game.Platform.GlDepthMask(false);
        // Preserve global depth order across effects and materials. Only adjacent
        // particles with the same material share a draw call.
        particles=particles.OrderBy(p=>view[2]*p.Position.X+view[6]*p.Position.Y+view[10]*p.Position.Z).ToArray();
        var lights=new Dictionary<(int,int,int),Vector3>();
        Vector3 Light(CreationSimulatedParticle p)
        {
            var world=p.Position+session.Project!.Origin;var cell=((int)Math.Floor(world.X),(int)Math.Floor(world.Y),(int)Math.Floor(world.Z));
            if(lights.TryGetValue(cell,out var value))return value;
            var native=api.World.BlockAccessor.GetLightRGBs(new BlockPos(cell.Item1,cell.Item2+session.Project.Dimension*BlockPos.DimensionBoundary,cell.Item3));var ambient=api.Render.AmbientColor;
            return lights[cell]=new(Math.Max(native.X,native.W*ambient.X),Math.Max(native.Y,native.W*ambient.Y),Math.Max(native.Z,native.W*ambient.Z));
        }
        try
        {
            foreach(var batch in DirectorParticleMesh.Build(particles,facing,Light,p=>states[p.ObjectId].Source.Opacity))
            {
                var m=batch.Material;BindTexture(session.Project!,m.Texture);api.Render.GlToggleBlend(true,m.Additive?EnumBlendMode.Glow:EnumBlendMode.Standard);
                shader.Uniform("particleMask",m.Soft&&!m.Cube?1:0);shader.Uniform("surfaceShading",m.Cube?1:0);shader.Uniform("glow",(float)m.Glow);
                shader.Uniform("particleUv",m.Uv.X,m.Uv.Y,m.Uv.Z,m.Uv.W);
                DrawParticleMesh(batch.Mesh);
            }
        }
        finally{api.Render.GlToggleBlend(true,EnumBlendMode.Standard);shader.Uniform("vertexColorMode",0);shader.Uniform("particleMask",0);}
    }
    internal static Matrix4x4 ParticleFacingMatrix(CreationSimulatedParticle p,Matrix4x4 camera)
    {
        if(p.Facing==ParticleFacing.Camera)return camera;
        if(p.Facing==ParticleFacing.CameraY){var flat=new Vector3(camera.M31,0,camera.M33);var forward=flat.LengthSquared()<1e-10?Vector3.UnitZ:Vector3.Normalize(flat);return Matrix4x4.CreateWorld(Vector3.Zero,forward,Vector3.UnitY);}
        if(p.Facing==ParticleFacing.Velocity&&p.Velocity.Float().LengthSquared()>1e-8)
        {var forward=Vector3.Normalize(p.Velocity.Float());return Matrix4x4.CreateWorld(Vector3.Zero,forward,Math.Abs(forward.Y)>.99?Vector3.UnitX:Vector3.UnitY);}
        return p.Facing switch{ParticleFacing.X=>Matrix4x4.CreateRotationY(MathF.PI/2),ParticleFacing.Y=>Matrix4x4.CreateRotationX(MathF.PI/2),_=>Matrix4x4.Identity};
    }
    private void RenderParticleRibbons(IReadOnlyDictionary<Guid,CreationObjectState> states)
    {
        if(ParticleRibbons.Count==0)return;
        shader!.Uniform("vertexColorMode",1);shader.Uniform("tint",1f,1f,1f,1f);shader.Uniform("particleMask",0);shader.Uniform("surfaceShading",0);
        shader.UniformMatrix("modelMatrix",Array(Matrix4x4.Identity));game.Platform.GlDepthMask(false);
        var view=game.PerspectiveViewMat;var direction=new Vector3((float)view[2],(float)view[6],(float)view[10]);
        foreach(var ribbon in ParticleRibbons)
        {
            if(!states.TryGetValue(ribbon.ObjectId,out var state)||!state.Visible)continue;
            BindTexture(session.Project!,ribbon.Texture);api.Render.GlToggleBlend(true,ribbon.Additive?EnumBlendMode.Glow:EnumBlendMode.Standard);shader.Uniform("glow",(float)ribbon.Glow);
            Vector3 Light(CreationVector point){var world=point+session.Project!.Origin;var v=api.World.BlockAccessor.GetLightRGBs(new BlockPos((int)world.X,(int)world.Y,(int)world.Z,session.Project.Dimension));var ambient=api.Render.AmbientColor;return new(Math.Max(v.X,v.W*ambient.X),Math.Max(v.Y,v.W*ambient.Y),Math.Max(v.Z,v.W*ambient.Z));}
            var mesh=DirectorParticleMesh.Ribbon(ribbon,direction,Light,state.Source.Opacity);
            DrawParticleMesh(mesh);
        }
        api.Render.GlToggleBlend(true,EnumBlendMode.Standard);shader.Uniform("vertexColorMode",0);
    }
}
