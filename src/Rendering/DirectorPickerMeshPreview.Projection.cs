using System.Numerics;
using OpenTK.Graphics.OpenGL4;
using VSDirector.Blueprints;
using VSDirector.Data;

namespace VSDirector.Rendering;

internal sealed partial class DirectorPickerMeshPreview
{
    internal CreationProjectionView? ProjectionView;
    private readonly List<(int Texture,float[] Vertices,ParticleMaterial? Material)> projectionBatches=[];
    private Vector3 projectionCenter;
    private float projectionRadius;
    internal bool LockProjectionBounds;
    internal void SetSize(int width,int height)
    {
        if(width==targetWidth&&height==targetHeight)return;
        if(width is <16 or >4096||height is <16 or >4096)throw new ArgumentOutOfRangeException(nameof(width));
        Dispose();targetWidth=width;targetHeight=height;
    }
    internal void SetProjection(IReadOnlyList<BlueprintMeshPart> parts,Matrix4x4 transform)
    {
        projectionBatches.Clear();var lo=new Vector3(float.MaxValue);var hi=new Vector3(float.MinValue);
        foreach(var part in parts.OrderBy(p=>p.Transparent))
        {
            var mesh=part.Mesh.Clone();
            for(int i=0;i<mesh.VerticesCount;i++)
            {
                var p=Vector3.Transform(new(mesh.xyz[i*3],mesh.xyz[i*3+1],mesh.xyz[i*3+2]),transform);
                mesh.xyz[i*3]=p.X;mesh.xyz[i*3+1]=p.Y;mesh.xyz[i*3+2]=p.Z;lo=Vector3.Min(lo,p);hi=Vector3.Max(hi,p);
            }
            SetMesh(mesh);
            mesh.Dispose();
            foreach(var batch in batches)projectionBatches.Add((batch.Texture,batch.Vertices,part.Transparent?new ParticleMaterial("",false,false,true,0,new(0,0,1,1)):null));
        }
        if(!LockProjectionBounds)
        {
            projectionCenter=(lo+hi)*.5f;projectionRadius=Math.Max(.1f,(hi-lo).Length()*.5f);
            if(parts.Count==0){projectionCenter=Vector3.Zero;projectionRadius=1;}
        }
        SetProjectionParticles([],0,_=>0);
    }
    internal void SetProjectionParticles(IReadOnlyList<CreationSimulatedParticle> particles,double time,Func<string,int> textures,IReadOnlyList<CreationParticleRibbon>? ribbons=null)
    {
        SetParticles(particles,time,textures,ribbons);
        var dynamic=batches.ToArray();batches.Clear();batches.AddRange(projectionBatches);batches.AddRange(dynamic);
        center=projectionCenter;radius=projectionRadius;
    }
    internal (float[] View,float[] Projection) InteractionMatrices(double time,float aspect)
    {
        var view=Matrix4x4.CreateTranslation(-center)*PreviewTurn(time);
        float r=radius/(.9f*(float)(ProjectionView?.Zoom??1));
        float perspective=ProjectionView?.Perspective==true?-.3f/r:0;
        float panX=(float)(ProjectionView?.PanX??0),panY=(float)(ProjectionView?.PanY??0);
        var projection=new Matrix4x4(Math.Min(1,1/aspect)/r,0,0,0,0,Math.Min(1,aspect)/r,0,0,panX*perspective,panY*perspective,-.5f/r,perspective,panX,panY,0,1);
        return(DirectorProjectionRenderFrame.Matrix(view),DirectorProjectionRenderFrame.Matrix(projection));
    }
    internal byte[] ReadRgba()
    {
        using var state=new DirectorCreationRenderState();
        int pack=GL.GetInteger(GetPName.PixelPackBufferBinding),alignment=GL.GetInteger(GetPName.PackAlignment);
        try
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer,0);GL.PixelStore(PixelStoreParameter.PackAlignment,1);
            GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,texture);
            var pixels=new byte[targetWidth*targetHeight*4];GL.GetTexImage(TextureTarget.Texture2D,0,PixelFormat.Rgba,PixelType.UnsignedByte,pixels);
            return pixels;
        }
        finally{GL.BindBuffer(BufferTarget.PixelPackBuffer,pack);GL.PixelStore(PixelStoreParameter.PackAlignment,alignment);}
    }
}
