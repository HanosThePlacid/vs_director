using System.Numerics;
using ImGuiNET;
using VSDirector.Data;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private Guid particleTimeDrag;
    private ParticleCurvePoint? particleKeyDrag;
    private double particleTimeStart,particleTimeDuration,particleTimeMouse;
    private int particleTimeEdge;
    private void DrawParticleNodeTracks(CreationSceneObject obj,float x0,float width,float labelWidth,float rowHeight,Func<double,float> x,Func<float,double> time)
    {
        if(obj.ParticleEffect is not {} effect)return;
        foreach(var node in effect.Emitters)
        {
            ImGui.PushID(node.Id.ToString());var row=ImGui.GetCursorScreenPos();
            ImGui.InvisibleButton("particle-track",new(width,rowHeight));var draw=ImGui.GetWindowDrawList();
            draw.PushClipRect(row,row+new Vector2(labelWidth,rowHeight),true);draw.AddText(row+new Vector2(8,3),ImGui.GetColorU32(ImGuiCol.Text),node.Name);draw.PopClipRect();
            float start=x(obj.ParticleStart+node.Start),end=x(obj.ParticleStart+node.Start+node.Duration);
            draw.PushClipRect(new(x0+labelWidth,row.Y),new(x0+width,row.Y+rowHeight),true);
            draw.AddRectFilled(new(start,row.Y+4),new(Math.Max(start+5,end),row.Y+rowHeight-4),node.Id==particleEmitter?0xffbb9966:0xff776644,2);
            draw.PopClipRect();
            if(ImGui.IsItemClicked())
            {
                Select(obj);particleEmitter=node.Id;particlesPanel=true;focusParticles=true;
                if(ImGui.GetIO().MousePos.X>=Math.Max(start,x0+labelWidth)&&ImGui.GetIO().MousePos.X<=end)
                {Session.BeginGesture();particleTimeDrag=node.Id;particleTimeStart=node.Start;particleTimeDuration=node.Duration;particleTimeMouse=time(ImGui.GetIO().MousePos.X);particleTimeEdge=Math.Abs(ImGui.GetIO().MousePos.X-end)<8?1:Math.Abs(ImGui.GetIO().MousePos.X-start)<8?-1:0;}
            }
            if(particleTimeDrag==node.Id&&ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {
                double delta=time(ImGui.GetIO().MousePos.X)-particleTimeMouse;
                if(particleTimeEdge==0)node.Start=Math.Clamp(Snap(particleTimeStart+delta),0,86400-node.Duration);
                else if(particleTimeEdge==1)node.Duration=Math.Clamp(Snap(particleTimeDuration+delta),0,86400-node.Start);
                else{node.Start=Math.Clamp(Snap(particleTimeStart+delta),0,particleTimeStart+particleTimeDuration);node.Duration=particleTimeStart+particleTimeDuration-node.Start;}
                obj.ParticleDuration=Math.Max(obj.ParticleDuration,node.Start+node.Duration);Session.Changed();
            }
            foreach(var (parameter,driver) in node.Modules?.Drivers??[])
            {
                if(driver.Mode!=ParticleValueMode.Curve||driver.Clock==ParticleClock.Life)continue;
                ImGui.PushID(parameter);row=ImGui.GetCursorScreenPos();ImGui.InvisibleButton("parameter-track",new(width,rowHeight));
                draw.PushClipRect(row,row+new Vector2(labelWidth,rowHeight),true);draw.AddText(row+new Vector2(18,3),ImGui.GetColorU32(ImGuiCol.TextDisabled),parameter);draw.PopClipRect();
                double offset=driver.Clock==ParticleClock.Project?0:obj.ParticleStart+node.Start;
                draw.PushClipRect(new(x0+labelWidth,row.Y),new(x0+width,row.Y+rowHeight),true);
                foreach(var key in driver.Curve.Points)
                {
                    var center=new Vector2(x(key.Time+offset),row.Y+rowHeight/2);draw.AddQuadFilled(center+new Vector2(0,-4),center+new Vector2(4,0),center+new Vector2(0,4),center+new Vector2(-4,0),0xff66ccff);
                    if(ImGui.IsItemClicked()&&Vector2.Distance(ImGui.GetIO().MousePos,center)<10){Select(obj);particleEmitter=node.Id;particleDriver=parameter;particleKeyDrag=key;Session.BeginGesture();}
                }
                draw.PopClipRect();
                if(particleKeyDrag is {} dragged&&driver.Curve.Points.Contains(dragged)&&ImGui.IsMouseDragging(ImGuiMouseButton.Left))
                {int index=driver.Curve.Points.IndexOf(dragged);dragged.Time=Math.Clamp(Snap(time(ImGui.GetIO().MousePos.X)-offset),index==0?0:driver.Curve.Points[index-1].Time+.001,index==driver.Curve.Points.Count-1?86400:driver.Curve.Points[index+1].Time-.001);Session.Changed();}
                if(ImGui.BeginPopupContextItem("particle-key-menu"))
                {
                    if(ImGui.MenuItem(P("add-key")))Session.Edit(P("add-key"),_=>{double t=Math.Max(0,Session.Time-offset);if(driver.Curve.Points.All(k=>Math.Abs(k.Time-t)>.001)){double value=driver.Curve.Sample(t);driver.Curve.Points.Add(new(){Time=t,Value=value});driver.Curve.Points.Sort((a,b)=>a.Time.CompareTo(b.Time));}});
                    if(ImGui.MenuItem(P("edit-curve"))){particleEmitter=node.Id;particleDriver=parameter;particleCurveOpen=true;}
                    ImGui.EndPopup();
                }
                ImGui.PopID();
            }
            ImGui.PopID();
        }
        if(ImGui.IsMouseReleased(ImGuiMouseButton.Left)&&(particleTimeDrag!=Guid.Empty||particleKeyDrag is not null))
        {Session.EndGesture(P("schedule"));particleTimeDrag=Guid.Empty;particleKeyDrag=null;}
    }
}
