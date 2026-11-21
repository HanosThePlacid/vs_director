using System.Numerics;
using HarmonyLib;
using ImGuiNET;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using VSDirector.Blueprints;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckProjectionEditor(object workspace,DirectorCreationSession session,object runtime)
    {
        var previous=session.Project!;var selected=session.SelectedObject;
        var root=new CreationSceneObject{Kind=CreationObjectKind.Structure,Name="Projection",Projection=new(){Version=2,Size=new(2,1,1)}};
        string path=Path.Combine(session.Store.Root,"projection-probe.vsbp");
        var document=new BlueprintDocument{Schematic=JObject.Parse("""
            {"SizeX":2,"SizeY":1,"SizeZ":1,"Indices":[0,1],"BlockIds":[1,1],"BlockCodes":{"1":"game:stone"}}
            """),Metadata=new(){Dimensions=new(2,1,1)}};
        new BlueprintCodec().Write(path,document);
        var project=new DirectorCreationProject{Objects=[root]};session.Open(project);session.SelectedObject=root.Id;
        root.AssetId=session.Store.Import(project,path,CreationAssetKind.Structure).Id;
        var service=AccessTools.Property(runtime.GetType(),"Blueprints").GetValue(runtime)!;
        var build=AccessTools.Method(service.GetType(),"Get",[typeof(DirectorCreationProject),typeof(CreationSceneObject)]).Invoke(service,[project,root])!;
        var bt=build.GetType();AccessTools.Field(bt,"Ready").SetValue(build,true);
        AccessTools.Field(bt,"Document").SetValue(build,document);AccessTools.Field(bt,"Schematic").SetValue(build,JsonUtil.FromString<BlockSchematic>(document.Schematic.ToString()));
        ProjectionCell[] cells=[new(0,0,default,"game:stone"),new(1,1,new(1,0,0),"game:stone")];
        AccessTools.Field(bt,"Cells").SetValue(build,cells);
        var type=workspace.GetType();var selection=(HashSet<int>)AccessTools.Field(type,"projectionSelection").GetValue(workspace)!;selection.Add(0);
        AccessTools.Field(type,"projectionAnchor").SetValue(workspace,0);
        root.Projection.Parameter("growth").Track.Keys.Add(new(){Time=1,Value=CreationVector.One});
        var io=ImGui.GetIO();var display=io.DisplaySize;string locale=Lang.CurrentLocale;float scale=RuntimeEnv.GUIScale;
        try
        {
            io.DisplaySize=new(1600,1600);
            foreach(string language in new[]{"en","zh-cn"})
            foreach(float zoom in new[]{1f,1.5f,2f})
            foreach(float width in new[]{220f,320f,520f})
            foreach(string page in new[]{"cells","range","growth","materials","particles","physics","keys","files"})
            {
                LoadCreationProbeLanguage(language);RuntimeEnv.GUIScale=zoom;AccessTools.Field(type,"projectionPage").SetValue(workspace,page);
                for(int frame=0;frame<3;frame++)
                {
                    ImGui.NewFrame();ImGui.SetNextWindowPos(Vector2.Zero);ImGui.SetNextWindowSize(new(width,1400));
                    ImGui.Begin("projection-properties-probe",ImGuiWindowFlags.NoSavedSettings);ImGui.SetWindowFontScale(zoom);
                    AccessTools.Method(type,"DrawProjectionEditor").Invoke(workspace,[root]);
                    if(frame==2&&ImGui.GetScrollMaxX()>1)throw new Exception($"Projection editor overflow: {language}/{zoom}/{width}/{page}: {ImGui.GetScrollMaxX()}");
                    ImGui.End();ImGui.Render();
                }
            }
            AccessTools.Method(type,"SyncContentSelection").Invoke(workspace,null);
            var sections=((IEnumerable<string>)AccessTools.Method(type,"ContentSections").Invoke(workspace,null)!).ToHashSet();
            foreach(var field in ProjectionFields.All)
            {
                string section=(string)AccessTools.Method(type,"ProjectionContentSection").Invoke(null,[field.Id])!;
                if(!sections.Contains(section))throw new Exception("Projection field has no content entry: "+field.Id);
            }
            string before=System.Text.Json.JsonSerializer.Serialize(project,CreationJson.Options);
            foreach(string language in new[]{"en","zh-cn"})
            foreach(float zoom in new[]{1f,1.5f,2f})
            foreach(float width in new[]{240f,360f,520f})
            foreach(string section in sections.Where(s=>s.StartsWith("b.")&&s is not ("b.preview" or "b.export" or "b.files")))
            {
                LoadCreationProbeLanguage(language);RuntimeEnv.GUIScale=zoom;
                AccessTools.Method(type,"NavigateContent").Invoke(workspace,[section,Guid.Empty,""]);
                for(int frame=0;frame<3;frame++)
                {
                    ImGui.NewFrame();ImGui.SetNextWindowPos(Vector2.Zero);ImGui.SetNextWindowSize(new(width,950));
                    ImGui.Begin("projection-content-probe",ImGuiWindowFlags.NoSavedSettings);ImGui.SetWindowFontScale(zoom);
                    AccessTools.Method(type,"DrawContentProperties").Invoke(workspace,null);
                    if(frame==2&&ImGui.GetScrollMaxX()>1)throw new Exception($"Projection content overflow: {language}/{zoom}/{width}/{section}");
                    ImGui.End();ImGui.Render();
                }
            }
            if(before!=System.Text.Json.JsonSerializer.Serialize(project,CreationJson.Options))throw new Exception("Projection browsing mutated data");
            // Embedded schemes edit the structure-owned effect without adding a fake scene object.
            var scheme=CreationParticleEffect.Create();root.Projection.ParticleSchemes["Test"]=scheme;
            var view=AccessTools.Field(type,"contentView").GetValue(workspace)!;
            AccessTools.Property(view.GetType(),"Scheme").SetValue(view,"Test");
            AccessTools.Method(type,"NavigateContent").Invoke(workspace,["p.motion",scheme.Emitters[0].Id,""]);
            var proxy=(CreationSceneObject)AccessTools.Property(type,"ContentParticleObject").GetValue(workspace)!;
            if(!ReferenceEquals(proxy.ParticleEffect,scheme)||project.Objects.Count!=1)throw new Exception("Embedded scheme is detached from its owner");
            session.Edit("Edit scheme",_=>proxy.ParticleEffect!.Seed=42);
            session.Undo();AccessTools.Method(type,"SyncContentSelection").Invoke(workspace,null);
            var restored=(CreationSceneObject)AccessTools.Property(type,"ContentParticleObject").GetValue(workspace)!;
            if(restored.ParticleEffect!.Seed==42||ReferenceEquals(proxy.ParticleEffect,restored.ParticleEffect))throw new Exception("Scheme undo did not rebind editor target");
            session.Open(project);session.SelectedObject=root.Id;
            AccessTools.Property(view.GetType(),"Scheme").SetValue(view,"");
            session.Edit("Move block",_=>root.Projection.Edit(cells[0]).Offset=new(2,0,0));
            session.Undo();if(session.Project!.Objects[0].Projection!.Edits.Count!=0)throw new Exception("Cell edit undo failed");
            session.Redo();if(session.Project!.Objects[0].Projection!.Edits[0].Offset.X!=2)throw new Exception("Cell edit redo failed");
            Console.WriteLine("PASS blueprint editor pages in English/Chinese at 220/320/520px and 100/150/200%; block undo/redo");
        }
        finally
        {
            RuntimeEnv.GUIScale=scale;LoadCreationProbeLanguage(locale);io.DisplaySize=display;
            selection.Clear();AccessTools.Field(type,"projectionPage").SetValue(workspace,"cells");session.Open(previous);session.SelectedObject=selected;
            File.Delete(path);
        }
    }
}
