using System.Collections;
using System.Numerics;
using System.Text.Json;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Config;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckContentNavigation(object workspace,DirectorCreationSession session,Action<string> capture)
    {
        var previous=session.Project!;var selected=session.SelectedObject;
        var type=workspace.GetType();var io=ImGui.GetIO();var display=io.DisplaySize;
        string locale=Lang.CurrentLocale;float scale=RuntimeEnv.GUIScale;
        object? Call(string name,params object?[] args)=>AccessTools.Method(type,name).Invoke(workspace,args);
        object View()=>AccessTools.Field(type,"contentView").GetValue(workspace)!;
        string Section()=>(string)AccessTools.Property(View().GetType(),"Section").GetValue(View())!;
        void Navigate(string section,Guid emitter=default,string field="")=>Call("NavigateContent",section,emitter,field);
        void Frame(string method,float width,bool inspect=false)
        {
            ImGui.NewFrame();ImGui.SetNextWindowPos(Vector2.Zero);ImGui.SetNextWindowSize(new(width,950));
            ImGui.Begin("##content-navigation-check",ImGuiWindowFlags.NoSavedSettings);
            ImGui.SetWindowFontScale(RuntimeEnv.GUIScale);Call(method);
            if(inspect&&ImGui.GetScrollMaxX()>1)throw new Exception($"Content overflow: {method}/{Section()}/{locale}/{width}/{RuntimeEnv.GUIScale}");
            ImGui.End();ImGui.Render();
        }
        var particle=new CreationSceneObject{Kind=CreationObjectKind.Particle,Name="Fire",ParticleEffect=CreationParticleEffect.Create()};
        particle.ParticleEffect.Emitters.Add(new(){Name="Smoke",Modules=new()});
        var camera=new CreationSceneObject{Kind=CreationObjectKind.Camera,Name="Camera"};
        var project=new DirectorCreationProject{Objects=[particle,camera],CameraId=camera.Id};
        session.Open(project);session.SelectedObject=particle.Id;
        try
        {
            Call("SyncContentSelection");
            if(Section()!="overview")throw new Exception("First selection must open overview");
            var sections=((IEnumerable<string>)Call("ContentSections")!).ToHashSet();
            foreach(var field in CreationParticleFields.All)
            {
                string target=(string)AccessTools.Method(type,"NativeContentSection").Invoke(null,[field.Name])!;
                if(!sections.Contains(target))throw new Exception("Native field has no content entry: "+field.Name);
            }
            foreach(string module in new[]{"shape","schedule","forces","material","geometry"})
            foreach(string field in (string[])AccessTools.Method(type,"ParticleModuleFields").Invoke(null,[module])!)
            {
                string target=(string)AccessTools.Method(type,"ModuleContentSection").Invoke(null,[module,field])!;
                if(!sections.Contains(target))throw new Exception("Module field has no content entry: "+field);
            }
            Guid emitter=particle.ParticleEffect.Emitters[1].Id;
            Navigate("p.motion",emitter);
            AccessTools.Field(type,"particleScope").SetValue(workspace,2);
            session.SelectedObject=camera.Id;Call("SyncContentSelection");
            Navigate("object.transform");
            session.SelectedObject=particle.Id;Call("SyncContentSelection");
            if(Section()!="p.motion"||(Guid)AccessTools.Field(type,"particleEmitter").GetValue(workspace)! !=emitter)
                throw new Exception("Per-object section/emitter memory was lost");
            if((int)AccessTools.Field(type,"particleScope").GetValue(workspace)! !=0)throw new Exception("Batch scope leaked across objects");
            string before=JsonSerializer.Serialize(project,CreationJson.Options);long revision=session.Revision;
            foreach(string language in new[]{"zh-cn","en"})
            foreach(float zoom in new[]{1f,1.5f,2f})
            foreach(float width in new[]{240f,360f,520f})
            {
                LoadCreationProbeLanguage(language);RuntimeEnv.GUIScale=zoom;io.DisplaySize=new(1920,1080);
                foreach(string section in sections.Where(s=>s.StartsWith("p.")&&s!="p.files"))
                {
                    Navigate(section,emitter);
                    for(int frame=0;frame<3;frame++)Frame("DrawContentProperties",width,frame==2);
                }
                for(int frame=0;frame<3;frame++)Frame("DrawContentBrowser",width,frame==2);
            }
            RuntimeEnv.GUIScale=1;LoadCreationProbeLanguage("zh-cn");io.DisplaySize=new(960,640);
            Navigate("p.emission",emitter);
            for(int frame=0;frame<3;frame++)
            {
                ImGui.NewFrame();
                foreach(var panel in new[]{("DrawBrowser",new Vector2(0,0),new Vector2(260,190)),("DrawContentBrowser",new Vector2(0,190),new Vector2(260,450)),("DrawContentProperties",new Vector2(580,0),new Vector2(380,640))})
                {
                    ImGui.SetNextWindowPos(panel.Item2);ImGui.SetNextWindowSize(panel.Item3);
                    ImGui.Begin(panel.Item1+"##content-snapshot",ImGuiWindowFlags.NoSavedSettings);Call(panel.Item1);ImGui.End();
                }
                ImGui.Render();
            }
            capture("particle-content");
            if(revision!=session.Revision||before!=JsonSerializer.Serialize(project,CreationJson.Options))
                throw new Exception("Browsing content changed the document");
            RuntimeEnv.GUIScale=1;LoadCreationProbeLanguage("en");
            AccessTools.Property(View().GetType(),"Search").SetValue(View(),"GravityEffect");
            for(int frame=0;frame<3;frame++)Frame("DrawContentBrowser",420);
            Navigate("p.motion",emitter,"GravityEffect");
            Frame("DrawContentProperties",420);
            if((string)AccessTools.Field(type,"contentField").GetValue(workspace)! != "")throw new Exception("Search did not reveal the requested field");
            // Navigation state must also be serializable without holding live document references.
            Call("SaveContentView");
            var preferences=AccessTools.Field(type,"editorPreferences").GetValue(workspace)!;
            string preferencesJson=JsonSerializer.Serialize(preferences,preferences.GetType(),CreationJson.Options);
            var restored=JsonSerializer.Deserialize(preferencesJson,preferences.GetType(),CreationJson.Options)!;
            if(((IDictionary)AccessTools.Property(restored.GetType(),"ContentViews").GetValue(restored)!).Count<2)
                throw new Exception("Navigation memory did not round trip");
            session.Edit("Remove emitter",p=>p.Objects[0].ParticleEffect!.Emitters.RemoveAll(n=>n.Id==emitter));
            Call("SyncContentSelection");
            if((Guid)AccessTools.Field(type,"particleEmitter").GetValue(workspace)! ==emitter)throw new Exception("Deleted emitter remains targeted");
            session.Undo();Call("SyncContentSelection");
            Navigate("p.motion",emitter);
            if(ReferenceEquals(AccessTools.Property(type,"ContentParticleObject").GetValue(workspace),particle))
                throw new Exception("Undo retained a stale object reference");
            Console.WriteLine("PASS content field coverage, per-object navigation memory, scope isolation, read-only browsing, deletion/undo and zh/en narrow layouts at 100/150/200%");
        }
        finally
        {
            io.MouseDown[0]=false;RuntimeEnv.GUIScale=scale;LoadCreationProbeLanguage(locale);io.DisplaySize=display;
            session.Open(previous);session.SelectedObject=selected;Call("SyncContentSelection");
        }
    }
}
