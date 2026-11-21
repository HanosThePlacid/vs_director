using System.Numerics;
using ImGuiNET;
using VSDirector.Blueprints;
using VSDirector.Data;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private DirectorProjectionScene? projectionScene;
    private DirectorProjectionExport? projectionExport;
    private Guid projectionObject,projectionProject,projectionPreviousSelection;
    private long projectionRevision;
    private bool projectionWasPlaying;
    private string projectionPreviousSection="overview",projectionPreviousScheme="";
    private double? projectionPendingElapsed;
    private double projectionPreviousTime,projectionClock,projectionElapsed;
    private void OpenProjectionPreview(CreationSceneObject obj,bool select=true)
    {
        if(Project is not {} project)return;
        var build=Runtime.Blueprints.Get(project,obj);
        if(build.Error.Length>0)throw new InvalidOperationException(build.Error);
        if(!build.Ready)throw new InvalidOperationException(B("loading"));
        if(build.Parts.Count==0)throw new InvalidOperationException(B("failed"));
        if(projectionScene is null)
        {
            SyncContentSelection();projectionPreviousSection=contentView.Section;projectionPreviousScheme=contentView.Scheme;
            projectionWasPlaying=Session.Playing;projectionPreviousTime=Session.Time;projectionPreviousSelection=Session.SelectedObject;projectionProject=project.Id;
            Session.Play(false);
        }
        projectionScene?.Dispose();projectionObject=obj.Id;
        projectionScene=new(api,project,obj.Id,build,source=>Runtime.Renderer.ParticleTexture(project,source));
        projectionRevision=Session.Revision;projectionClock=ImGui.GetTime();projectionElapsed=0;projectionPendingElapsed=null;
        if(select){Select(obj);NavigateContent("b.preview");}
    }
    private void CloseProjection()
    {
        projectionExport?.Dispose();projectionExport=null;projectionScene?.Dispose();projectionScene=null;
        if(Project?.Id==projectionProject){Session.SelectedObject=projectionPreviousSelection;Session.Seek(projectionPreviousTime);Session.Play(projectionWasPlaying);SyncContentSelection();contentView.Section=projectionPreviousSection;contentView.Scheme=projectionPreviousScheme;contentScrollKey="";contentValidatedRevision=-1;}
        projectionObject=projectionProject=Guid.Empty;
    }
    private void SaveProjectionView()
    {
        if(projectionScene is null||Project?.Objects.Find(o=>o.Id==projectionObject)?.Projection is not {} settings)return;
        var view=CreationJson.Clone(projectionScene.View);view.Validate();
        Session.Edit(B("display"),_=>settings.View=view);projectionRevision=Session.Revision;
    }
    private void DrawProjectionUI()
    {
        if(projectionScene is null)return;
        if(Project?.Id!=projectionProject||!Runtime.ProjectWorldMatches||Project.Objects.Find(o=>o.Id==projectionObject) is not {} obj){CloseProjection();return;}
        if(projectionExport is {} export)
        {
            export.Tick();
            if(export.Done){error=export.Error.Length>0?export.Error:B("export-done");export.Dispose();projectionExport=null;}
        }
        if(projectionScene.Build.Cancelled){CloseProjection();return;}
        if(projectionRevision!=Session.Revision)Run(()=>OpenProjectionPreview(obj,false));
        var scene=projectionScene!;
        var min=host.ViewportPosition;var size=host.ViewportSize;
        if(size.X<16||size.Y<16)return;
        ImGui.SetNextWindowPos(min);ImGui.SetNextWindowSize(size);
        bool open=true;
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding,Vector2.Zero);
        bool visible=ImGui.Begin(B("preview")+"###projection-stage",ref open,ImGuiWindowFlags.NoDecoration|ImGuiWindowFlags.NoDocking|ImGuiWindowFlags.NoSavedSettings|ImGuiWindowFlags.NoMove|ImGuiWindowFlags.NoBringToFrontOnFocus);
        ImGui.PopStyleVar();
        if(visible)
        {
            double now=ImGui.GetTime();double next=projectionElapsed+Math.Clamp(now-projectionClock,0,.05);projectionClock=now;
            if(next>=scene.View.Duration)next=0;
            try
            {
                projectionPendingElapsed??=next;next=projectionPendingElapsed.Value;
                if(scene.Render(next,Math.Clamp((int)size.X,16,2048),Math.Clamp((int)size.Y,16,2048))){projectionElapsed=next;projectionPendingElapsed=null;}
                if(scene.View.Transparent)
                {
                    var draw=ImGui.GetWindowDrawList();float cell=20*DirectorImGuiTheme.Scale;
                    for(float y=0;y<size.Y;y+=cell)for(float x=0;x<size.X;x+=cell)
                        draw.AddRectFilled(min+new Vector2(x,y),Vector2.Min(min+size,min+new Vector2(x+cell,y+cell)),((int)(x/cell)+(int)(y/cell))%2==0?0xff292929:0xff333333);
                }
                ImGui.Image((nint)scene.Renderer.Texture,size,new(0,1),new(1,0));
                bool hovered=ImGui.IsItemHovered();var io=ImGui.GetIO();
                if(projectionExport is null&&hovered)
                {
                    if(!projectionCellMode&&ImGui.IsMouseDragging(ImGuiMouseButton.Left)){scene.View.Rotate=false;scene.View.Yaw+=io.MouseDelta.X*.4;scene.View.Pitch=Math.Clamp(scene.View.Pitch+io.MouseDelta.Y*.4,-89,89);}
                    if(ImGui.IsMouseDragging(ImGuiMouseButton.Middle)){scene.View.PanX+=io.MouseDelta.X/size.X*2;scene.View.PanY-=io.MouseDelta.Y/size.Y*2;}
                    if(io.MouseWheel!=0){scene.View.Zoom=Math.Clamp(scene.View.Zoom*Math.Pow(1.1,io.MouseWheel),.05,20);SaveProjectionView();}
                    if(ImGui.IsMouseReleased(ImGuiMouseButton.Left)||ImGui.IsMouseReleased(ImGuiMouseButton.Middle))SaveProjectionView();
                }
                if(projectionCellMode&&projectionExport is null)DrawProjectionViewport(scene,min,size,projectionElapsed);
                if(ImGui.BeginPopupContextItem("projection-stage-menu"))
                {
                    if(ImGui.MenuItem(B("reset"))){ResetProjectionView();}
                    if(ImGui.MenuItem(B("exit")))CloseProjection();ImGui.EndPopup();
                }
            }
            catch(Exception e){error=e.GetBaseException().Message;ImGui.TextWrapped(error);}
        }
        ImGui.End();
        if(!open)CloseProjection();
    }
    private void ResetProjectionView()
    {
        if(projectionScene is not {} scene)return;
        scene.View.Yaw=45;scene.View.Pitch=-35.264;scene.View.Zoom=1;scene.View.PanX=scene.View.PanY=0;SaveProjectionView();
    }
    private void DrawProjectionTools()
    {
        var scene=projectionScene!;bool first=true;
        if(projectionCellMode)
        {
            foreach(var entry in new[]{(CreationTool.Select,"cursor"),(CreationTool.Move,"arrows-move"),(CreationTool.Rotate,"rotate"),(CreationTool.Scale,"arrows-maximize")})
            {
                if(!first)ImGui.SameLine();first=false;
                using(DirectorImGuiTheme.Button(ActiveTool==entry.Item1?"active":"normal"))
                    if(DirectorImGuiIcons.Button(DirectorImGuiIcons.Get(entry.Item2)+"##projection-tool-"+entry.Item1,new(ImGui.GetFrameHeight())))ChooseTool(entry.Item1);
            }
            ImGui.NewLine();first=true;
        }
        if(InlineChoice(B("display"),Session.SelectedObject==projectionObject,ref first)&&Project?.Objects.Find(o=>o.Id==projectionObject) is {} obj){Select(obj);NavigateContent("b.preview");}
        if(InlineChoice(B("reset"),false,ref first))ResetProjectionView();
        if(InlineChoice(B("rotate"),scene.View.Rotate,ref first)){scene.View.Rotate=!scene.View.Rotate;SaveProjectionView();}
        if(InlineChoice(B("exit"),false,ref first))CloseProjection();
    }
    private void DrawProjectionProperties()
    {
        if(Project?.Objects.Find(o=>o.Id==projectionObject) is {} obj)DrawProjectionViewContent(obj,contentView.Section=="b.export");
    }
    private void DrawProjectionProgress(float width)
    {
        if(width<=0)return;
        using var style=DirectorImGuiTheme.Toolbar();
        ImGui.SetCursorPos(new(ImGui.GetWindowWidth()-ImGui.GetStyle().WindowPadding.X-width,Math.Max(0,(ImGui.GetWindowHeight()-ImGui.GetFrameHeight())*.5f)));
        ImGui.ProgressBar((float)(projectionExport?.Progress??Runtime.ProjectionProgress),new(width,ImGui.GetFrameHeight()));
        if(ImGui.BeginPopupContextItem("projection-progress")){if(ImGui.MenuItem(L("creation-cancel-export"))){projectionExport?.Dispose();projectionExport=null;Runtime.CancelProjectionRebuild();}ImGui.EndPopup();}
    }
}
