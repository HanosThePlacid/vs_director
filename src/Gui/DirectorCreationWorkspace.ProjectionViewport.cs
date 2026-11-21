using System.Numerics;
using ImGuiNET;
using VSDirector.Data;
using VSDirector.Blueprints;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private Dictionary<int,(ProjectionCell Cell,ProjectionCellEdit Edit,Matrix4x4 Matrix)>? projectionDragCells;
    private Matrix4x4 projectionDragPivot;
    private bool DrawProjectionViewport(DirectorProjectionScene? scene=null,Vector2 stageMin=default,Vector2 stageSize=default,double elapsed=0)
    {
        if(SelectedObject is not {Projection:{} s} obj||!projectionCellMode||EditableProjection(obj) is not {} build||Session.Frame?.Objects.GetValueOrDefault(obj.Id) is not {} state)return false;
        var (min,size)=scene is null?GameImageRect():(stageMin,stageSize);
        var objectWorld=scene?.CurrentWorld??state.World;
        var view=DirectorCreationMath.ProjectView(api.Render.PerspectiveViewMat,Project!.Origin,Project.Dimension*(double)Vintagestory.API.MathTools.BlockPos.DimensionBoundary);
        var projection=api.Render.PerspectiveProjectionMat.Select(v=>(float)v).ToArray();
        if(scene is not null)(view,projection)=scene.Renderer.InteractionMatrices(elapsed,size.X/size.Y);
        Matrix4x4 Matrix(float[] m)=>new(m[0],m[1],m[2],m[3],m[4],m[5],m[6],m[7],m[8],m[9],m[10],m[11],m[12],m[13],m[14],m[15]);
        var vp=Matrix(view)*Matrix(projection);var draw=ImGui.GetForegroundDrawList();draw.PushClipRect(min,min+size);
        Vector2? Screen(Vector3 p){var v=Vector4.Transform(new Vector4(p,1),vp);return v.W>.001?min+new Vector2(v.X/v.W+1,1-v.Y/v.W)*size*.5f:null;}
        void Box(Matrix4x4 transform,Vector3 a,Vector3 b,uint color,bool fill)
        {
            var points=new Vector2?[8];for(int i=0;i<8;i++)points[i]=Screen(Vector3.Transform(new((i&1)==0?a.X:b.X,(i&2)==0?a.Y:b.Y,(i&4)==0?a.Z:b.Z),transform));
            if(fill)foreach(int[] face in new int[][]{[0,1,3,2],[4,5,7,6],[0,4,5,1],[2,3,7,6],[0,2,6,4],[1,5,7,3]})
                if(face.All(i=>points[i].HasValue))draw.AddQuadFilled(points[face[0]]!.Value,points[face[1]]!.Value,points[face[2]]!.Value,points[face[3]]!.Value,(color&0xffffff)|0x20000000);
            for(int i=0;i<8;i++)for(int bit=1;bit<=4;bit*=2)if((i&bit)==0&&points[i] is {} p&&points[i|bit] is {} q)draw.AddLine(p,q,color,1.5f);
        }
        var evaluated=scene?.CurrentCells??(Runtime.Renderer.ProjectionFrames.TryGetValue(obj.Id,out var frame)?frame.Cells:build.BaseFrame);
        if(projectionDragCells is not null&&ImGui.IsKeyPressed(ImGuiKey.Escape,false))
        {Session.CancelGesture();projectionDragCells=null;handleAxis=handleHover=-1;gizmoUsing=false;draw.PopClipRect();return true;}
        foreach(var cell in evaluated.Where(c=>projectionSelection.Contains(c.Cell.Id)).Take(6000))Box(cell.Matrix*objectWorld,Vector3.Zero,Vector3.One,0xff22ccff,false);
        if(s.RangeEnabled)Box(objectWorld,Vector3.Min(s.RangeMin.Float(),s.RangeMax.Float()),Vector3.Max(s.RangeMin.Float(),s.RangeMax.Float())+Vector3.One,0xff44cc88,true);
        if(projectionDragCells is not null&&!ImGui.IsMouseDown(ImGuiMouseButton.Left))
        {Session.EndGesture(L("creation-gizmo-transform"));projectionDragCells=null;handleAxis=-1;gizmoUsing=false;}
        var primary=evaluated.FirstOrDefault(c=>c.Cell.Id==projectionAnchor&&projectionSelection.Contains(c.Cell.Id))??evaluated.FirstOrDefault(c=>projectionSelection.Contains(c.Cell.Id));
        if(primary is not null&&!selectionTool)
        {
            // Handles edit the authored pose. Animation and physics are shown separately by the outline.
            var cell=primary.Cell;var edit=s.Edits.GetValueOrDefault(cell.Id)??new ProjectionCellEdit{Source=cell.Source};
            var center=(DirectorProjectionEditing.Position(cell,s)+new CreationVector(.5,.5,.5)).Float();
            var local=Matrix4x4.CreateScale(edit.Scale.Float())*CreationTransform.RotationMatrix(edit.Rotation)*Matrix4x4.CreateTranslation(center);
            var pivot=projectionDragCells is null?local*objectWorld:projectionDragPivot;
            bool changed=ManipulateCreation(draw,pivot,view,projection,min,size,false,out var manipulated);
            gizmoHover=handleHover>=0;gizmoUsing=handleAxis>=0;
            if(gizmoUsing&&projectionDragCells is null)
            {
                Session.BeginGesture();projectionDragPivot=pivot;
                projectionDragCells=DirectorProjectionEditing.Cells(build.Cells,s).Where(c=>projectionSelection.Contains(c.Id)).ToDictionary(c=>c.Id,c=>
                {
                    var e=CreationJson.Clone(s.Edits.GetValueOrDefault(c.Id)??new ProjectionCellEdit{Source=c.Source});
                    var m=Matrix4x4.CreateScale(e.Scale.Float())*CreationTransform.RotationMatrix(e.Rotation)*Matrix4x4.CreateTranslation((c.Position+e.Offset+new CreationVector(.5,.5,.5)).Float());
                    return(c,e,m);
                });
            }
            if(changed&&projectionDragCells is not null&&Matrix4x4.Invert(projectionDragPivot,out var invPivot)&&Matrix4x4.Invert(objectWorld,out var invWorld))
            {
                var delta=objectWorld*invPivot*manipulated*invWorld;
                foreach(var (id,start) in projectionDragCells)
                {
                    var transform=DirectorCreationMath.FromMatrix(start.Matrix*delta,start.Edit.Rotation,start.Edit.Scale);
                    var e=s.Edit(start.Cell);e.Offset=transform.Position-new CreationVector(.5,.5,.5)-start.Cell.Position;e.Rotation=transform.Rotation;
                    e.Scale=new(Math.Clamp(transform.Scale.X,.25,4),Math.Clamp(transform.Scale.Y,.25,4),Math.Clamp(transform.Scale.Z,.25,4));
                }
                Session.Changed();
            }
        }
        if(selectionTool){handleHover=handleAxis=-1;gizmoHover=gizmoUsing=false;}
        draw.PopClipRect();
        bool hovered=ImGui.IsMouseHoveringRect(min,min+size)&&!ImGui.IsPopupOpen("",ImGuiPopupFlags.AnyPopupId|ImGuiPopupFlags.AnyPopupLevel);
        if(hovered&&handleHover<0&&handleAxis<0&&ImGui.IsMouseClicked(ImGuiMouseButton.Left)&&Matrix4x4.Invert(vp,out var inverse))
        {
            var mouse=(ImGui.GetIO().MousePos-min)/size;var a=Vector4.Transform(new Vector4(mouse.X*2-1,1-mouse.Y*2,-1,1),inverse);var b=Vector4.Transform(new Vector4(mouse.X*2-1,1-mouse.Y*2,.9f,1),inverse);
            Vector3 start=new(a.X/a.W,a.Y/a.W,a.Z/a.W),end=new(b.X/b.W,b.Y/b.W,b.Z/b.W);var direction=Vector3.Normalize(end-start);
            float nearest=float.PositiveInfinity;int? hit=null;
            foreach(var cell in evaluated.Where(c=>c.Visible))
            {
                if(!Matrix4x4.Invert(cell.Matrix*objectWorld,out var inv))continue;var o=Vector3.Transform(start,inv);var d=Vector3.TransformNormal(direction,inv);float near=0,far=float.PositiveInfinity;
                for(int axis=0;axis<3;axis++){float p=axis==0?o.X:axis==1?o.Y:o.Z,v=axis==0?d.X:axis==1?d.Y:d.Z;if(Math.Abs(v)<1e-8){if(p<0||p>1)far=-1;continue;}float t0=-p/v,t1=(1-p)/v;near=Math.Max(near,Math.Min(t0,t1));far=Math.Min(far,Math.Max(t0,t1));}
                if(far>=near&&near<nearest){nearest=near;hit=cell.Cell.Id;}
            }
            if(hit is {} id){if(!ImGui.GetIO().KeyCtrl)projectionSelection.Clear();if(!projectionSelection.Add(id))projectionSelection.Remove(id);projectionAnchor=id;NavigateContent("b.cells");}
        }
        return true;
    }
}
