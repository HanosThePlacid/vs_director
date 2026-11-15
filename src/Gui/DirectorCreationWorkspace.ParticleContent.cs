using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using VSDirector.Data;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private string? nativeContentSection;
    private HashSet<string>? particleModuleFieldSubset;
    private bool particleJsonOpen,particleGradientOpen;
    private static string NativeContentSection(string field) => field switch
    {
        "Quantity"=>"p.emission", "PosOffset"=>"p.shape", "LifeLength"=>"p.environment",
        "ParticleModel" or "VertexFlags"=>"p.material",
        _ when field.EndsWith("Evolve",StringComparison.Ordinal)=>"p.evolution",
        _=>"p."+(CreationParticleFields.All.FirstOrDefault(f=>f.Name==field)?.Group switch {"appearance"=>"color",{} group=>group,_=>"files"})
    };
    private static string[] ParticleModuleFields(string page) => page switch
    {
        "shape"=>["Extent","Surface","DirectionSpeed","MeshSource","MeshParticle"],
        "schedule"=>["LoopPeriod","DistanceInterval","Probability","Delay","InheritVelocity"],
        "forces"=>["Acceleration","Drag","ForceCenter","Attraction","Vortex","Noise","Parametric","InitialRotation","Spin","SpinAcceleration","Friction","KillPlane","KillHeight","ExpireBlocks","ExpireOutsideBlocks","ColorBlock"],
        "material"=>["Width","Height","UvOrigin","UvSize","Columns","Rows","Frames","FramesPerSecond","AnimateByLife","TextureLoop"],
        "geometry"=>["End","RibbonWidth","Retain","SampleDistance","Segments","Bend","Jitter","Branches","FlickerRate","TextureFlow","ClipAtTerrain","FadeEnds"],_=>[]
    };
    private static bool EnvironmentModuleField(string field)=>field is "Friction" or "KillPlane" or "KillHeight" or "ExpireBlocks" or "ExpireOutsideBlocks";
    private static string ModuleContentSection(string page,string field)=>page switch
    {"schedule"=>"p.emission","forces"=>EnvironmentModuleField(field)?"p.environment":"p.motion",_=>"p."+page};
    private void DrawParticleContent(CreationSceneObject obj)
    {
        var effect=obj.ParticleEffect!;string section=contentView.Section;
        if(section=="p.files"){DrawParticleFiles(obj);return;}
        if(effect.Version<3)
        {
            Hint(P("upgrade-hint"));
            if(Button(P("upgrade")))Session.Edit(P("upgrade"),_=>effect.Upgrade());
        }
        if(section=="p.overview")
        {
            if(contentView.Scheme.Length==0)DrawProjectionParticleSource(obj);
            TextField(L("creation-name"),effect.Name,v=>effect.Name=v);
            Number(L("creation-particle-seed"),effect.Seed,v=>effect.Seed=(int)v,0,int.MaxValue,1);
            Number(P("game-speed"),effect.GameSpeed,v=>effect.GameSpeed=v,.05,200,.05f);
            Number(P("density"),effect.Density,v=>effect.Density=v,0,100,.05f);
            if(contentView.Scheme.Length==0)
            {
                TimeNumber(P("effect-start"),obj.ParticleStart,v=>obj.ParticleStart=v,0,86400);
                TimeNumber(P("effect-duration"),obj.ParticleDuration,v=>obj.ParticleDuration=v,0,86400);
            }
            return;
        }
        var emitter=effect.AllNodes().FirstOrDefault(e=>e.Id==particleEmitter)??effect.Emitters.FirstOrDefault();
        if(emitter is null){Hint(N("no-emitter"));return;}
        particleEmitter=emitter.Id;
        void Edit(Action<CreationParticleEmitter> set){foreach(var node in ParticleTargets(effect,emitter))set(node);}
        if(section=="p.emission")
        {
            TextField(L("creation-name"),emitter.Name,v=>emitter.Name=v);
            Toggle(L("creation-enabled"),emitter.Enabled,v=>Edit(n=>n.Enabled=v));
            if(effect.Emitters.Contains(emitter))
            {
                TimeNumber(L("creation-start-time"),emitter.Start,v=>Edit(n=>n.Start=v),0,86400);
                TimeNumber(L("creation-emission-duration"),emitter.Duration,v=>Edit(n=>n.Duration=v),0,86400);
                Number(L("creation-particle-interval"),emitter.Interval,v=>Edit(n=>n.Interval=v),.001,86400,.01f);
                Toggle(L("creation-particle-burst"),emitter.Burst,v=>Edit(n=>n.Burst=v));
            }
        }
        if(section=="p.shape")
        {
            VectorField(L("creation-particle-offset"),emitter.Offset,v=>Edit(n=>n.Offset=v),.01f,inline:true);
            Toggle(L("creation-particle-local-follow"),emitter.LocalFollow,v=>Edit(n=>n.LocalFollow=v));
        }
        if(section=="p.motion")Number(L("creation-particle-parent-velocity"),emitter.ParentVelocityWeight,v=>Edit(n=>n.ParentVelocityWeight=v),0,1000,.01f);
        if(section=="p.color")DrawParticleColor(effect,emitter);
        // Native JSON descriptors remain the authoritative field list, including legacy child definitions.
        nativeContentSection=section;
        try{DrawNativeParticleFields(effect,emitter,emitter.Properties,[],0);}
        finally{nativeContentSection=null;}
        if(section=="p.material")
        {
            TextField(L("creation-particle-texture"),emitter.Texture,v=>Edit(n=>n.Texture=v.Trim()));
            if(Button(L("creation-particle-import-texture")))FileDialog(L("creation-particle-import-texture"),file=>
            {Session.Edit(L("creation-particle-import-texture"),p=>{var source=Session.Store.Import(p,file,CreationAssetKind.Texture).Source;Edit(n=>n.Texture=source);});Runtime.Renderer.ReloadTextures();});
            Toggle(L("creation-particle-soft"),emitter.SoftEdge,v=>Edit(n=>n.SoftEdge=v));
            Toggle(L("creation-particle-additive"),emitter.Additive,v=>Edit(n=>n.Additive=v));
            if(Header(N("flags")))DrawParticleFlags(effect,emitter);
        }
        if(section=="p.children" && effect.Version>=3)DrawParticleChildActions(effect,emitter);
        if(effect.Version>=3 && emitter.Modules is {} modules)
        {
            string? module=section switch {"p.emission"=>"schedule","p.shape"=>"shape","p.motion" or "p.environment"=>"forces", "p.material"=>"material","p.geometry"=>"geometry","p.drivers"=>"drivers",_=>null};
            if(section=="p.color" && Button(P("gradient")))particleGradientOpen=true;
            if(section=="p.evolution")
            {
                ImGui.Separator();
                if(Button(N("drivers")))NavigateContent("p.drivers",emitter.Id);
                foreach(string field in modules.Drivers.Keys.ToArray())
                    if(Button(P(field)+"##driver-"+field)){particleDriver=field;NavigateContent("p.drivers",emitter.Id);}
            }
            if(module is not null)
            {
                particleModulePage=module;
                if(module=="forces")particleModuleFieldSubset=ParticleModuleFields(module).Where(f=>EnvironmentModuleField(f)==(section=="p.environment")).ToHashSet();
                bool restricted=section=="p.geometry"&&contentView.Scheme.Length>0;
                if(restricted)Hint(Q("scheme-geometry"));
                ImGui.BeginDisabled(restricted);
                try{ImGui.PushID("module");DrawParticleModules(obj,emitter);ImGui.PopID();}
                finally{particleModuleFieldSubset=null;ImGui.EndDisabled();}
            }
        }
        ImGui.Separator();
        if(Button(N("edit-json"))){particleJsonOpen=true;particleJsonId="";}
    }
    private void DrawParticleGradientWindow()
    {
        if(!particleGradientOpen||Runtime.Exporting||auxiliary.Length>0)return;
        var effect=ContentParticleObject?.ParticleEffect;
        var emitter=effect?.AllNodes().FirstOrDefault(n=>n.Id==particleEmitter);
        if(emitter?.Modules is not {} module){particleGradientOpen=false;return;}
        ImGui.SetNextWindowSize(new Vector2(480,430)*DirectorImGuiTheme.Scale,ImGuiCond.FirstUseEver);
        if(ImGui.Begin(P("gradient")+"###creation-particle-gradient",ref particleGradientOpen))
        {
            ImGui.TextWrapped(ContentParticleObject!.Name+" / "+emitter.Name);
            DrawParticleGradient(module);
        }
        ImGui.End();
    }
    private void DrawParticleJsonWindow()
    {
        if(!particleJsonOpen||Runtime.Exporting||auxiliary.Length>0)return;
        var obj=ContentParticleObject;var effect=obj?.ParticleEffect;
        var emitter=effect?.AllNodes().FirstOrDefault(n=>n.Id==particleEmitter);
        if(effect is null||emitter is null){particleJsonOpen=false;return;}
        string id=ContentKey+"/"+contentView.Scheme+"/"+emitter.Id;
        if(particleJsonId!=id){particleJsonId=id;particleJson=emitter.Properties.ToJsonString(CreationJson.Options);}
        ImGui.SetNextWindowSize(new Vector2(620,460)*DirectorImGuiTheme.Scale,ImGuiCond.FirstUseEver);
        if(ImGui.Begin(N("edit-json")+"###creation-particle-json",ref particleJsonOpen))
        {
            ImGui.TextWrapped(obj!.Name+" / "+emitter.Name);
            if(Button(L("creation-particle-json-refresh")))particleJson=emitter.Properties.ToJsonString(CreationJson.Options);
            ImGui.InputTextMultiline("##json",ref particleJson,1024*1024,new(-1,Math.Max(40,ImGui.GetContentRegionAvail().Y-ImGui.GetFrameHeightWithSpacing()*2)));
            if(Button(L("creation-apply")))Run(()=>
            {var node=JsonNode.Parse(particleJson) as JsonObject??throw new InvalidDataException("Expected particle object.");CreationParticleFields.Validate(node);Session.Edit(L("creation-apply"),_=>SetParticleNode(effect,emitter,[],node));});
            if(error.Length>0)ImGui.TextWrapped(error);
        }
        ImGui.End();
    }
}
