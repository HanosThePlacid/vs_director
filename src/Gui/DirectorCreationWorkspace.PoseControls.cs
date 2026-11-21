using System.Numerics;
using ImGuiNET;
using VSDirector.Data;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private readonly List<(Guid Part,Vector2 Point,Vector3 World)> poseHandles=new();
    private Guid directPosePart;
    private DirectorCreationDrag? directPoseDrag;
    private Vector3 directPoseStart;
    private CreationObjectState? directPoseState;
    private IReadOnlyList<CreationPart>? directPoseChain;
    private readonly Dictionary<Guid,(CreationPart Upper,CreationPart Lower)[]> poseHandleChains=new();
    private DirectorCreationProject? poseHandleProject;
    private long poseHandleRevision=-1;
    private (CreationPart Upper,CreationPart Lower)[] PoseControlChains(CreationModel model)
    {
        if(poseHandleRevision!=Session.Revision||!ReferenceEquals(poseHandleProject,Project))
        {poseHandleChains.Clear();poseHandleRevision=Session.Revision;poseHandleProject=Project;}
        if(!poseHandleChains.TryGetValue(model.Id,out var chains))
            poseHandleChains[model.Id]=chains=DirectorCreationIk.SuggestChains(model).ToArray();
        return chains;
    }
    private void DrawPoseInspector(bool ik)
    {
        if(!RigContext(out var actor,out var model))return;
        if(ik)
        {
            if(ActiveIkChain(actor) is not {} chain)return;
            ImGui.TextWrapped(chain.Name);
            bool first=true;
            foreach(var control in new[]{(chain.Target,"creation-ik-target"),(chain.Pole,"creation-ik-pole")})
                if(InlineChoice(L(control.Item2),Session.SelectedObject==control.Item1,ref first)
                    &&Project!.Objects.Find(o=>o.Id==control.Item1) is {} obj)Select(obj);
            if(SelectedObject is {} selected)DrawRigTransform(selected,Guid.Empty,positionOnly:true);
            if(Button(L("creation-ik-configure"))){ikPanel=true;focusRigPanel="ik";}
            if(Session.Frame?.Objects.GetValueOrDefault(actor.Id)?.IkWeight<=0)
            {Hint(L("creation-ik-pose-owned"));if(Button(L("creation-scene-pose")))StartScenePose(actor);}
            return;
        }
        if(Session.HasPoseDraft)
        {
            if(Button(L("creation-record-preview")))Run(Session.CommitTransformDrafts);
            SameLineIfFits(L("creation-revert-preview"));if(Button(L("creation-revert-preview")))Session.ClearTransformDrafts();
        }
        if(model.Parts.Find(p=>p.Id==Session.SelectedPart) is {} bone)
        {ImGui.SeparatorText(BoneName(model,bone));DrawRigTransform(actor,bone.Id);}
        else Hint(L("creation-pose-handles-help"));
        if(Button(L("creation-bones-tab"))){bonesPanel=true;focusRigPanel="bones";}
        SameLineIfFits(L("creation-ik-configure"));
        if(Button(L("creation-ik-configure"))){ikPanel=true;focusRigPanel="ik";}
    }
    private void DrawAutomaticPoseHandles(ImDrawListPtr draw,CreationSceneObject actor,CreationModel model,CreationObjectState state,
        Matrix4x4 view,Matrix4x4 vp,Vector2 min,Vector2 size,Func<Vector3,Vector2?> screen)
    {
        if(!Overlay.PoseHandles||!state.Visible||state.HasPerformance)return;
        float radius=Overlay.Size*DirectorImGuiTheme.Scale;
        foreach(var pair in PoseControlChains(model))
        {
            var part=pair.Lower;
            if(state.HiddenParts.Contains(part.Id)||!state.Bones.TryGetValue(part.Id,out var matrix))continue;
            var world=Vector3.Transform(part.Group?DirectorCreationMath.BonePivot(part,state.Pose.GetValueOrDefault(part.Id)??new(),model.AnimationVersion):((part.To-part.From)*(1d/32)).Float(),matrix*state.World);
            if(screen(world) is not {} point||point.X<min.X||point.Y<min.Y||point.X>min.X+size.X||point.Y>min.Y+size.Y)continue;
            poseHandles.Add((part.Id,point,world));
            uint color=(Overlay.TargetColor&0xffffff)|((uint)(Overlay.Opacity*255)<<24);
            draw.AddQuadFilled(point+new Vector2(0,-radius),point+new Vector2(radius,0),point+new Vector2(0,radius),point+new Vector2(-radius,0),color);
        }
        bool interactive=!ImGui.IsPopupOpen("",ImGuiPopupFlags.AnyPopupId|ImGuiPopupFlags.AnyPopupLevel)
            &&ImGui.IsMouseHoveringRect(min,min+size)
            &&(!ImGui.IsWindowHovered(ImGuiHoveredFlags.AnyWindow)||ImGui.IsWindowHovered(ImGuiHoveredFlags.RootAndChildWindows));
        var hit=poseHandles.Where(h=>{var d=Vector2.Abs(h.Point-ImGui.GetIO().MousePos);return d.X+d.Y<=radius;})
            .OrderBy(h=>Vector2.DistanceSquared(h.Point,ImGui.GetIO().MousePos)).FirstOrDefault();
        if(interactive&&hit.Part!=Guid.Empty&&!gizmoUsing)
        {
            gizmoHover=true;
            if(model.Parts.Find(p=>p.Id==hit.Part) is {} hovered)
            {var at=Vector2.Clamp(hit.Point+new Vector2(radius+5,5),min,Vector2.Max(min,min+size-ImGui.CalcTextSize(BoneName(model,hovered))-new Vector2(8)));draw.AddText(at,0xffffffff,BoneName(model,hovered));}
            if(ImGui.IsMouseClicked(ImGuiMouseButton.Left))
            {
                SelectBone(actor.Id,hit.Part);ChooseTool(CreationTool.Pull);
                directPoseDrag=DirectorCreationDrag.Begin(vp,min,size,ImGui.GetIO().MousePos,hit.World,new(view.M13,view.M23,view.M33),true);
                if(directPoseDrag is not null)
                {
                    Session.BeginGesture(poseOnly:true);directPosePart=hit.Part;directPoseStart=hit.World;
                    directPoseState=new(){Source=state.Source,World=state.World};
                    foreach(var entry in state.Pose)directPoseState.Pose[entry.Key]=entry.Value with{};
                    DirectorCreationEvaluator.EvaluateBones(model,directPoseState);
                    var pair=PoseControlChains(model).First(c=>c.Lower.Id==hit.Part);directPoseChain=[pair.Upper,pair.Lower];gizmoUsing=true;
                }
            }
        }
        if(directPosePart!=Guid.Empty&&directPoseDrag is not null&&directPoseState is not null&&ImGui.IsMouseDragging(ImGuiMouseButton.Left)
            &&directPoseDrag.TryOffset(ImGui.GetIO().MousePos,out var offset))
        {
            if(ImGui.GetIO().KeyShift)offset*=.1f;
            foreach(var pair in DirectorCreationPoseGeometry.Pull(model,directPoseState,directPosePart,directPoseStart+offset,directPoseChain!))Session.SetPosePart(pair.Key,pair.Value);
            Session.Changed();
        }
    }
}
