using System.Numerics;
using HarmonyLib;
using ImGuiNET;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckCreationCameraInteraction(object workspace,DirectorCreationSession session,Action frame)
    {
        var type=workspace.GetType();var io=ImGui.GetIO();
        object? Call(string name,params object[] args)=>AccessTools.Method(type,name).Invoke(workspace,args);
        void Tool(string name)=>Call("ChooseTool",Enum.Parse(AccessTools.Field(type,"objectTool").FieldType,name));
        var handles=AccessTools.Field(type,"creationHandles").GetValue(workspace)!;
        (Vector2 A,Vector2 B,int Axis,Guid CameraId)[] Lines()=>((IEnumerable<(Vector2 A,Vector2 B,int Axis,Guid CameraId)>)AccessTools.Property(handles.GetType(),"Lines").GetValue(handles)!).ToArray();
        foreach(bool animated in new[]{false,true})
        foreach(bool autoKey in new[]{false,true})
        foreach(bool bound in new[]{false,true})
        {
            session.ClearTransformDrafts();
            var camera=new CreationSceneObject{Kind=CreationObjectKind.Camera,Transform=new(){Position=new(.2,0,0)}};
            var project=new DirectorCreationProject{Objects=[camera],CameraId=camera.Id};
            if(animated)
            {
                DirectorCreationEdits.SetKey(project,camera.Id,Guid.Empty,CreationChannel.Position,0,new(.2,0,0));
                DirectorCreationEdits.SetKey(project,camera.Id,Guid.Empty,CreationChannel.Position,4,new(.8,0,0));
            }
            session.Open(project);session.AutoKey=autoKey;session.Seek(2);Call("Select",camera);Tool("Move");
            if(bound)
            {
                var parent=new CreationSceneObject{Kind=CreationObjectKind.Group,Transform=new(){Position=new(3,2,1),Rotation=new(0,65,0)}};
                project.Objects.Add(parent);session.Refresh();var original=session.Frame!.Objects[camera.Id].World;
                session.Reparent(camera.Id,parent.Id);
                if(Vector3.Distance(original.Translation,session.Frame!.Objects[camera.Id].World.Translation)>.002f)throw new Exception("Binding moved viewport camera");
            }
            AccessTools.Field(type,"transformMode").SetValue(workspace,ImGuizmoNET.MODE.WORLD);
            io.MousePos=new(1000,750);frame();frame();
            var shaft=Lines().First(l=>l.Axis==0&&Vector2.Distance(l.A,l.B)>20);
            io.MousePos=Vector2.Lerp(shaft.A,shaft.B,.7f);frame();frame();
            var start=session.Frame!.Objects[camera.Id].World;
            io.MouseDown[0]=true;frame();io.MousePos+=new Vector2(30,0);frame();io.MouseDown[0]=false;frame();
            var moved=session.Frame!.Objects[camera.Id].World;
            if(moved.M41<=start.M41+.01f||Math.Abs(moved.M42-start.M42)>.002f)throw new Exception($"Camera XYZ drag ineffective: animated={animated}, auto={autoKey}, start={start.Translation}, end={moved.Translation}");
            if(animated&&!session.AnimationEditing)throw new Exception("Animated camera remained in masked base editing mode");
            if(session.HasTransformDraft)session.CommitTransformDrafts();
            Tool("Rotate");AccessTools.Field(type,"objectRotationAxis").SetValue(workspace,6);
            io.MousePos=new(1000,750);frame();frame();var ring=Lines().Where(l=>l.Axis==6).ToArray();
            var before=session.Frame!.Objects[camera.Id].World;
            io.MousePos=ring[0].A;frame();frame();io.MouseDown[0]=true;frame();io.MousePos=ring[32].A;frame();io.MouseDown[0]=false;frame();
            var rotated=session.Frame!.Objects[camera.Id].World;
            if(Math.Abs(rotated.M12-before.M12)<.5f)throw new Exception($"Camera ring drag ineffective: animated={animated}, auto={autoKey}");
            if(session.HasTransformDraft)session.CommitTransformDrafts();
            session.Undo();frame();if(Math.Abs(session.Frame!.Objects[camera.Id].World.M12-before.M12)>.002f)throw new Exception("Camera rotation undo failed");
        }
        // Reproduce selecting a camera track while an actor pose is still active.
        var part=new CreationPart();var model=new CreationModel{Parts=[part]};
        var actor=new CreationSceneObject{ModelId=model.Id};var cam=new CreationSceneObject{Kind=CreationObjectKind.Camera};
        session.Open(new(){Objects=[actor,cam],Models=[model],CameraId=cam.Id});
        session.BeginScenePose(actor.Id);session.SelectedPart=part.Id;AccessTools.Field(type,"wholeObjectSelection").SetValue(workspace,false);
        session.SelectedObject=cam.Id;Call("SyncSelection");
        if(session.ScenePoseEditing||session.SelectedPart!=Guid.Empty||!(bool)AccessTools.Field(type,"wholeObjectSelection").GetValue(workspace)!)throw new Exception("Camera timeline selection retained actor bone scope");
        Console.WriteLine("PASS actual creation camera XYZ/ring pointer drags with/without tracks, auto/manual keys, undo and actor-to-camera timeline selection");
    }
}
