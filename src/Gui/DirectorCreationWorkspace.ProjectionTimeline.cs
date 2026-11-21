using System.Numerics;
using ImGuiNET;
using VSDirector.Data;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private CreationKeyframe? projectionDraggedKey;
    private void DrawProjectionTracks(CreationSceneObject obj,float x0,float width,float labelWidth,float rowHeight,Func<double,float> x,Func<float,double> time)
    {
        var s=obj.Projection!;
        foreach(string group in new[]{"growth","materials","particles","physics","cells"})
        {
            ImGui.PushID("projection-tracks-"+group);
            if(Header(Q(group)))
            {
                foreach(var (key,p) in s.Parameters.Where(p=>ProjectionFields.ById[p.Key].Group==group&&p.Value.Track.Keys.Count>0))Row(Q(key),p.Track,key,null);
                if(group=="cells"&&s.CellKeyframes)foreach(var (id,e) in s.Edits.Where(p=>projectionSelection.Contains(p.Key)||p.Value.Tracks.Any(t=>t.Keys.Count>0)))
                    foreach(var track in e.Tracks.Where(t=>t.Keys.Count>0))Row(id+" · "+Q("key-"+track.Channel),track,"",id);
                if(group=="materials")foreach(var choice in s.Glass.Select((c,i)=>(c,Q("glass")+" "+(i+1))).Concat(s.Edges.Select((c,i)=>(c,Q("edge")+" "+(i+1)))))ChoiceRow(choice.Item2,choice.c);
                if(group=="particles")ChoiceRow(Q("scheme"),s.ParticleScheme);
            }
            ImGui.PopID();
        }
        if(projectionDraggedKey is not null&&ImGui.IsMouseReleased(ImGuiMouseButton.Left)){Session.EndGesture(Q("time"));projectionDraggedKey=null;}
        void Row(string name,CreationTrack track,string field,int? cell)
        {
            ImGui.PushID(track.Id.ToString());var row=ImGui.GetCursorScreenPos();ImGui.InvisibleButton("track",new(width,rowHeight));var draw=ImGui.GetWindowDrawList();
            draw.PushClipRect(row,row+new Vector2(labelWidth,rowHeight),true);draw.AddText(row+new Vector2(12,3),ImGui.GetColorU32(ImGuiCol.Text),name);draw.PopClipRect();
            if(ImGui.IsItemClicked()){Select(obj);projectionResourceField="";projectionTrackField=field;projectionPage="keys";NavigateContent("b.keys");if(cell is {} id){projectionSelection.Clear();projectionSelection.Add(id);projectionAnchor=id;projectionCellChannel=track.Channel;}}
            draw.PushClipRect(new(x0+labelWidth,row.Y),new(x0+width,row.Y+rowHeight),true);
            foreach(var key in track.Keys)
            {
                var center=new Vector2(x(key.Time),row.Y+rowHeight*.5f);draw.AddQuadFilled(center+new Vector2(0,-4),center+new Vector2(4,0),center+new Vector2(0,4),center+new Vector2(-4,0),0xff66ccff);
                if(ImGui.IsItemClicked()&&Vector2.Distance(center,ImGui.GetIO().MousePos)<9){projectionDraggedKey=key;Session.BeginGesture();}
            }
            draw.PopClipRect();
            if(projectionDraggedKey is {} dragged&&track.Keys.Contains(dragged)&&ImGui.IsMouseDragging(ImGuiMouseButton.Left)){dragged.Time=Math.Clamp(Snap(time(ImGui.GetIO().MousePos.X)),0,86400);Session.Changed();}
            if(ImGui.BeginPopupContextItem("keys")){if(ImGui.MenuItem(Q("clear-keys")))Session.Edit(Q("clear-keys"),_=>track.Keys.Clear());ImGui.EndPopup();}ImGui.PopID();
        }
        void ChoiceRow(string name,ProjectionChoice choice)
        {
            if(choice.Keys.Count==0)return;ImGui.PushID(name);var row=ImGui.GetCursorScreenPos();ImGui.InvisibleButton("choice",new(width,rowHeight));var draw=ImGui.GetWindowDrawList();
            draw.PushClipRect(row,row+new Vector2(labelWidth,rowHeight),true);draw.AddText(row+new Vector2(12,3),ImGui.GetColorU32(ImGuiCol.Text),name);draw.PopClipRect();
            draw.PushClipRect(new(x0+labelWidth,row.Y),new(x0+width,row.Y+rowHeight),true);
            foreach(var key in choice.Keys){var p=new Vector2(x(key.Time),row.Y+rowHeight*.5f);draw.AddRectFilled(p-new Vector2(4),p+new Vector2(4),0xff88cc88);if(ImGui.IsItemClicked()&&Vector2.Distance(p,ImGui.GetIO().MousePos)<9)Session.Seek(key.Time);}
            draw.PopClipRect();
            if(ImGui.IsItemClicked())
            {
                Select(obj);projectionPage="keys";NavigateContent("b.keys");projectionResourceField=ReferenceEquals(choice,s.ParticleScheme)?"scheme":Array.IndexOf(s.Glass,choice) is int gi&&gi>=0?"glass"+gi:"edge"+Array.IndexOf(s.Edges,choice);
                ShowCreationPanel("properties");
            }
            if(ImGui.BeginPopupContextItem("choice-keys")){if(ImGui.MenuItem(Q("clear-keys")))Session.Edit(Q("clear-keys"),_=>choice.Keys.Clear());ImGui.EndPopup();}ImGui.PopID();
        }
    }
}
