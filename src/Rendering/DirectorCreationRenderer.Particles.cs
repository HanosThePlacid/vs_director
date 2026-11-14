using System.Numerics;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VSDirector.Data;

namespace VSDirector.Rendering;

internal sealed partial class DirectorCreationRenderer
{
    private void RenderAdvancedParticles(IReadOnlyDictionary<Guid,CreationObjectState> states)
    {
        var particles=AdvancedParticles.Where(p=>states.TryGetValue(p.ObjectId,out var state)&&state.Visible).ToArray();
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
        static (string Texture,bool Additive,bool Cube,bool Soft,double Glow) Material(CreationSimulatedParticle p)=>(p.Texture,p.Additive,p.Cube,p.Soft,p.Glow);
        var lights=new Dictionary<(int,int,int),Vector3>();
        try
        {
        for(int start=0;start<particles.Length;)
        {
            var material=Material(particles[start]);int end=start+1;
            while(end<particles.Length&&Material(particles[end])==material)end++;
            BindTexture(session.Project!,material.Texture);
            api.Render.GlToggleBlend(true,material.Additive?EnumBlendMode.Glow:EnumBlendMode.Standard);
            shader.Uniform("particleMask",material.Soft&&!material.Cube?1:0);
            shader.Uniform("surfaceShading",material.Cube?1:0);
            shader.Uniform("glow",(float)material.Glow);
            int vertices=(end-start)*(material.Cube?24:4);
            var data=new MeshData(vertices,(end-start)*(material.Cube?36:6),withFlags:false);
            data.CustomFloats=new CustomMeshDataPartFloat{Values=new float[vertices*3],InterleaveSizes=[3],InterleaveStride=12,InterleaveOffsets=[0],Count=vertices*3};
            for(int index=start;index<end;index++)
            {
                var p=particles[index];var state=states[p.ObjectId];
                var scale=p.ShapeScale==default?Vector3.One:p.ShapeScale.Float();
                var rotation=p.ShapeRotation==default?Matrix4x4.Identity:Matrix4x4.CreateFromQuaternion(p.ShapeRotation);
                var matrix=Matrix4x4.CreateScale(scale*(float)p.Size)*Matrix4x4.CreateRotationZ((float)(p.Rotation*Math.PI/180))
                    *(p.Cube?rotation:facing)*Matrix4x4.CreateTranslation(p.Position.Float());
                byte Byte(double n)=>(byte)Math.Clamp((int)Math.Round(n*255),0,255);
                var rgb=p.Color;
                if(p.LegacyProgress>=0 && state.Source.ParticleEffect?.Emitters.FirstOrDefault(e=>e.Id==p.EmitterId)?.Legacy is {} legacy)
                    rgb=state.Color*(1-p.LegacyProgress)+(legacy.ParticleSettings??CreationParticleSettings.Preset(legacy.ResourceCode)).EndColor*p.LegacyProgress;
                byte[] color=[Byte(rgb.X),Byte(rgb.Y),Byte(rgb.Z),Byte(p.Alpha*state.Source.Opacity)];
                var world=p.Position+session.Project!.Origin;
                var cell=((int)Math.Floor(world.X),(int)Math.Floor(world.Y),(int)Math.Floor(world.Z));
                if(!lights.TryGetValue(cell,out var light))
                {
                    var native=api.World.BlockAccessor.GetLightRGBs(new BlockPos(cell.Item1,cell.Item2+session.Project.Dimension*BlockPos.DimensionBoundary,cell.Item3));
                    var ambient=api.Render.AmbientColor;
                    lights[cell]=light=new(Math.Max(native.X,native.W*ambient.X),Math.Max(native.Y,native.W*ambient.Y),Math.Max(native.Z,native.W*ambient.Z));
                }
                void Face(Vector3[] vertices)
                {
                    int first=data.VerticesCount;
                    for(int i=0;i<4;i++)
                    {
                        var v=Vector3.Transform(vertices[i],matrix);int offset=data.VerticesCount*3;
                        data.CustomFloats.Values[offset]=light.X;data.CustomFloats.Values[offset+1]=light.Y;data.CustomFloats.Values[offset+2]=light.Z;
                        data.AddVertex(v.X,v.Y,v.Z,i is 1 or 2?1:0,i>=2?1:0,color);
                    }
                    foreach(int i in new[]{0,1,2,0,2,3})data.AddIndex(first+i);
                }
                if(!p.Cube)Face([new(-.5f,-.5f,0),new(.5f,-.5f,0),new(.5f,.5f,0),new(-.5f,.5f,0)]);
                else
                {
                    Vector3[] points=Enumerable.Range(0,8).Select(i=>new Vector3((i&1)==0?-.5f:.5f,(i&2)==0?-.5f:.5f,(i&4)==0?-.5f:.5f)).ToArray();
                    foreach(int[] face in new int[][]{[0,1,3,2],[4,6,7,5],[0,4,5,1],[2,3,7,6],[0,2,6,4],[1,5,7,3]})Face(face.Select(i=>points[i]).ToArray());
                }
            }
            using var mesh=api.Render.UploadMesh(data);game.Platform.RenderMesh(mesh);
            start=end;
        }
        }
        finally
        {
            api.Render.GlToggleBlend(true,EnumBlendMode.Standard);shader.Uniform("vertexColorMode",0);shader.Uniform("particleMask",0);
        }
    }
}
