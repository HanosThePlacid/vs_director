using System.Numerics;
using VSDirector.Data;
using Vintagestory.API.Client;

namespace VSDirector.Rendering;

internal readonly record struct ParticleMaterial(string Texture,bool Additive,bool Cube,bool Soft,double Glow,Vector4 Uv);
internal sealed record ParticleMeshBatch(ParticleMaterial Material,MeshData Mesh);

/// <summary>Identical particle geometry and UV mapping for catalogue, scene and export.</summary>
internal static class DirectorParticleMesh
{
    internal static List<ParticleMeshBatch> Build(IEnumerable<CreationSimulatedParticle> input,Matrix4x4 camera,Func<CreationSimulatedParticle,Vector3> light,Func<CreationSimulatedParticle,double>? opacity=null)
    {
        var result=new List<ParticleMeshBatch>();var particles=input.ToArray();
        ParticleMaterial Material(CreationSimulatedParticle p)=>new(p.Texture,p.Additive,p.Cube||p.Mesh is not null,p.Soft,p.Glow,p.Uv==default?new(0,0,1,1):p.Uv);
        for(int start=0;start<particles.Length;)
        {
            var material=Material(particles[start]);int end=start+1;while(end<particles.Length&&Material(particles[end])==material)end++;
            int count=particles[start..end].Sum(p=>p.Mesh?.Count??(material.Cube?24:4));int indices=particles[start..end].Sum(p=>p.Mesh?.Count??(material.Cube?36:6));var mesh=new MeshData(count,indices,withFlags:false);
            mesh.CustomFloats=new(){Values=new float[count*3],InterleaveSizes=[3],InterleaveStride=12,InterleaveOffsets=[0],Count=count*3};
            for(int index=start;index<end;index++)
            {
                var p=particles[index];var scale=p.ShapeScale==default?Vector3.One:p.ShapeScale.Float();var rotation=p.ShapeRotation==default?Matrix4x4.Identity:Matrix4x4.CreateFromQuaternion(p.ShapeRotation);
                var matrix=Matrix4x4.CreateScale(scale*(float)p.Size)*Matrix4x4.CreateRotationZ((float)(p.Rotation*Math.PI/180))*(p.Cube||p.Mesh is not null?rotation:DirectorCreationRenderer.ParticleFacingMatrix(p,camera))*Matrix4x4.CreateTranslation(p.Position.Float());
                byte Byte(double n)=>(byte)Math.Clamp(Math.Round(n*255),0,255);
                byte[] color=[Byte(p.Color.X),Byte(p.Color.Y),Byte(p.Color.Z),Byte(p.Alpha*(opacity?.Invoke(p)??1))];var lighting=light(p);
                void Face(Vector3[] corners)
                {
                    int first=mesh.VerticesCount;
                    for(int i=0;i<4;i++)
                    {
                        var v=Vector3.Transform(corners[i],matrix);int offset=mesh.VerticesCount*3;mesh.CustomFloats.Values[offset]=lighting.X;mesh.CustomFloats.Values[offset+1]=lighting.Y;mesh.CustomFloats.Values[offset+2]=lighting.Z;
                        mesh.AddVertex(v.X,v.Y,v.Z,i is 1 or 2?material.Uv.Z:material.Uv.X,i>=2?material.Uv.W:material.Uv.Y,color);
                    }
                    foreach(int i in new[]{0,1,2,0,2,3})mesh.AddIndex(first+i);
                }
                if(p.Mesh is {Count:>0} model)
                {
                    foreach(var point in model)
                    {
                        var v=Vector3.Transform(point.Float(),matrix);int vertexIndex=mesh.VerticesCount;
                        mesh.CustomFloats.Values[vertexIndex*3]=lighting.X;mesh.CustomFloats.Values[vertexIndex*3+1]=lighting.Y;mesh.CustomFloats.Values[vertexIndex*3+2]=lighting.Z;
                        mesh.AddVertex(v.X,v.Y,v.Z,(float)point.X,(float)point.Y,color);mesh.AddIndex(vertexIndex);
                    }
                }
                else if(!p.Cube)Face([new(-.5f,-.5f,0),new(.5f,-.5f,0),new(.5f,.5f,0),new(-.5f,.5f,0)]);
                else
                {
                    Vector3[] points=Enumerable.Range(0,8).Select(i=>new Vector3((i&1)==0?-.5f:.5f,(i&2)==0?-.5f:.5f,(i&4)==0?-.5f:.5f)).ToArray();
                    foreach(int[] face in new int[][]{[0,1,3,2],[4,6,7,5],[0,4,5,1],[2,3,7,6],[0,2,6,4],[1,5,7,3]})Face(face.Select(i=>points[i]).ToArray());
                }
            }
            result.Add(new(material,mesh));start=end;
        }
        return result;
    }
    internal static MeshData Ribbon(CreationParticleRibbon ribbon,Vector3 direction,Func<CreationVector,Vector3> light,double opacity=1)
    {
        var mesh=new MeshData(ribbon.Points.Length*2,(ribbon.Points.Length-1)*6,withFlags:false);
        mesh.CustomFloats=new CustomMeshDataPartFloat{Values=new float[ribbon.Points.Length*6],InterleaveSizes=[3],InterleaveStride=12,InterleaveOffsets=[0],Count=ribbon.Points.Length*6};
        byte Byte(double n)=>(byte)Math.Clamp(n*255,0,255);
        for(int i=0;i<ribbon.Points.Length;i++)
        {
            var lighting=light(ribbon.Points[i]);
            for(int sideIndex=0;sideIndex<2;sideIndex++){int offset=(i*2+sideIndex)*3;mesh.CustomFloats.Values[offset]=lighting.X;mesh.CustomFloats.Values[offset+1]=lighting.Y;mesh.CustomFloats.Values[offset+2]=lighting.Z;}
            var p=ribbon.Points[i].Float();var tangent=(ribbon.Points[Math.Min(i+1,ribbon.Points.Length-1)]-ribbon.Points[Math.Max(0,i-1)]).Float();
            var side=Vector3.Cross(tangent,direction);side=side.LengthSquared()<1e-10?Vector3.UnitX:Vector3.Normalize(side);side*=(float)(ribbon.Width*(ribbon.Widths?[i]??1))*.5f;
            var rgb=ribbon.Colors?[i]??ribbon.Color;
            byte[] color=[Byte(rgb.X),Byte(rgb.Y),Byte(rgb.Z),Byte((ribbon.Alphas?[i]??ribbon.Alpha)*opacity)];
            mesh.AddVertex(p.X-side.X,p.Y-side.Y,p.Z-side.Z,0,(float)(ribbon.Flow+(double)i/(ribbon.Points.Length-1)),color);
            mesh.AddVertex(p.X+side.X,p.Y+side.Y,p.Z+side.Z,1,(float)(ribbon.Flow+(double)i/(ribbon.Points.Length-1)),color);
            if(i>0){int offset=(i-1)*2;foreach(int v in new[]{0,1,3,0,3,2})mesh.AddIndex(offset+v);}
        }
        return mesh;
    }
}
