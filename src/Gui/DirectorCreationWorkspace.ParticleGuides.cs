using System.Numerics;
using ImGuiNET;
using VSDirector.Data;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private bool particleShowGuides=true,particleGuideDragging,particleGuideHover;
    private int particleGuidePoint;
    private float particleGuideDepth;
    private bool DrawParticleGuides(ImDrawListPtr draw,Matrix4x4 vp,Vector2 min,Vector2 size,Func<Vector3,Vector2?> screen,Action<Vector3,Vector3,uint> line)
    {
        particleGuideHover=false;
        if(!particleShowGuides||Runtime.Exporting||SelectedObject?.ParticleEffect?.AllNodes().FirstOrDefault(e=>e.Id==particleEmitter) is not {Modules:{} m} emitter
            ||Session.Frame?.Objects.GetValueOrDefault(m.BindObject==Guid.Empty?SelectedObject.Id:m.BindObject) is not {} state)return false;
        var matrix=state.World;if(m.BindBone!=Guid.Empty&&state.Bones.TryGetValue(m.BindBone,out var bone))matrix=bone*matrix;
        Vector3 World(CreationVector point)=>Vector3.Transform(point.Float(),matrix);
        void LocalLine(CreationVector a,CreationVector b)=>line(World(a+emitter.Offset),World(b+emitter.Offset),0xff80c9e0);
        if(m.Shape==ParticleSpawnShape.Box)
        {
            var corners=Enumerable.Range(0,8).Select(i=>new CreationVector((i&1)==0?-.5:.5,(i&2)==0?-.5:.5,(i&4)==0?-.5:.5)).Select(p=>new CreationVector(p.X*m.Extent.X,p.Y*m.Extent.Y,p.Z*m.Extent.Z)).ToArray();
            for(int i=0;i<8;i++)for(int axis=0;axis<3;axis++)if((i&(1<<axis))==0)LocalLine(corners[i],corners[i|(1<<axis)]);
        }
        else if(m.Shape is ParticleSpawnShape.Sphere or ParticleSpawnShape.Hemisphere or ParticleSpawnShape.Disc or ParticleSpawnShape.Cylinder or ParticleSpawnShape.Cone)
        {
            for(int plane=0;plane<(m.Shape is ParticleSpawnShape.Sphere or ParticleSpawnShape.Hemisphere?3:1);plane++)
            for(int i=0;i<48;i++)
            {
                CreationVector Point(double angle)=>plane switch{1=>new(Math.Cos(angle)*m.Extent.X,Math.Sin(angle)*m.Extent.Y,0),2=>new(0,Math.Cos(angle)*m.Extent.Y,Math.Sin(angle)*m.Extent.Z),_=>new(Math.Cos(angle)*m.Extent.X,0,Math.Sin(angle)*m.Extent.Z)};
                LocalLine(Point(i*Math.PI/24),Point((i+1)*Math.PI/24));
            }
        }
        else if(m.Shape==ParticleSpawnShape.Path)for(int i=1;i<m.Path.Count;i++)LocalLine(m.Path[i-1],m.Path[i]);
        if(m.Geometry is ParticleGeometry.Beam or ParticleGeometry.Lightning && m.EndObject==Guid.Empty)line(World(emitter.Offset),World(m.End),0xff80c9e0);
        var points=new List<CreationVector>{emitter.Offset};
        if(m.Geometry is ParticleGeometry.Beam or ParticleGeometry.Lightning && m.EndObject==Guid.Empty)points.Add(m.End);
        bool interactive=ImGui.IsMouseHoveringRect(min,min+size)&&(!ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow)||ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows));
        for(int i=0;i<points.Count;i++)if(screen(World(points[i])) is {} point)
        {
            draw.AddCircle(point,6*DirectorImGuiTheme.Scale,i==0?0xff80c9e0:0xff88dd88,12,2);
            if(interactive&&Vector2.Distance(point,ImGui.GetIO().MousePos)<9*DirectorImGuiTheme.Scale)
            {
                particleGuideHover=true;ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                if(ImGui.IsMouseClicked(ImGuiMouseButton.Left)){Session.BeginGesture();particleGuideDragging=true;particleGuidePoint=i;var clip=Vector4.Transform(new Vector4(World(points[i]),1),vp);particleGuideDepth=clip.Z/clip.W;}
            }
        }
        if(!particleGuideDragging)return particleGuideHover&&ImGui.IsMouseClicked(ImGuiMouseButton.Left);
        if(ImGui.IsKeyPressed(ImGuiKey.Escape)){Session.CancelGesture();particleGuideDragging=false;return true;}
        if(!ImGui.IsMouseDown(ImGuiMouseButton.Left)){Session.EndGesture(P("move-node"));particleGuideDragging=false;return true;}
        if(Matrix4x4.Invert(vp,out var inverse)&&Matrix4x4.Invert(matrix,out var local))
        {
            var pointer=(ImGui.GetIO().MousePos-min)/size;var projected=Vector4.Transform(new Vector4(pointer.X*2-1,1-pointer.Y*2,particleGuideDepth,1),inverse);
            if(Math.Abs(projected.W)>1e-8)
            {
                var position=Vector3.Transform(new Vector3(projected.X,projected.Y,projected.Z)/projected.W,local);
                if(ImGui.GetIO().KeyCtrl)position=new(MathF.Round(position.X*10)/10,MathF.Round(position.Y*10)/10,MathF.Round(position.Z*10)/10);
                if(particleGuidePoint==0)emitter.Offset=CreationVector.From(position);else m.End=CreationVector.From(position);Session.Changed();
            }
        }
        return true;
    }
}
