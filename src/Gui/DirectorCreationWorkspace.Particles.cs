using System.Numerics;
using System.Text.Json;
using ImGuiNET;
using VSDirector.Data;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private bool particlesPanel, focusParticles;
    private void DrawParticlePanel()
    {
        if(SelectedObject is not { Kind:CreationObjectKind.Particle } obj)
        { ImGui.TextWrapped(L("creation-particle-select")); return; }
        ImGui.TextWrapped(obj.Name);
        DrawParticleEditor(obj);
    }
    private void DrawParticleEditor(CreationSceneObject obj)
    {
        if(obj.ParticleEffect is not null){DrawAdvancedParticleEditor(obj);return;}
        if(LegacySectionVisible("files")&&Button(L("creation-particle-convert")))
        {Session.Edit(L("creation-particle-convert"),_=>obj.ParticleEffect=CreationParticleFields.ConvertLegacy(obj));return;}
        var settings=obj.ParticleSettings??CreationParticleSettings.Preset(obj.ResourceCode);
        // Do not dirty legacy works merely by opening their inspector.
        void Set(Action<CreationParticleSettings> edit) { obj.ParticleSettings=settings; edit(settings); }
        static string PresetName(string code)=>L(code switch { "smoke"=>"creation-smoke","snow"=>"creation-snow","glow"=>"creation-glow-particles",_=>"creation-sparks" });
        if(LegacySectionVisible("files")&&Combo(L("creation-particle-preset"),PresetName(obj.ResourceCode)))
        {
            foreach(string code in new[]{"smoke","sparks","snow","glow"})
                if(ImGui.Selectable(PresetName(code))) Session.Edit(L("creation-select-particles"),_=>
                { obj.ResourceCode=code;obj.ParticleSettings=CreationParticleSettings.Preset(code);obj.Glow=code is "sparks" or "glow"?1:0; });
            ImGui.EndCombo();
        }
        ImGui.TextWrapped(L("creation-particle-units"));
        if(LegacySectionVisible("emission")&&Header(L("creation-particle-emission")))
        {
            Toggle(L("creation-particle-burst"),settings.Burst,v=>Set(s=>s.Burst=v));
            TimeNumber(L("creation-start-time"),obj.ParticleStart,v=>obj.ParticleStart=v,0,Project!.Duration);
            if(!settings.Burst)TimeNumber(L("creation-emission-duration"),obj.ParticleDuration,v=>obj.ParticleDuration=v,0,86400);
            Number(L(settings.Burst?"creation-particle-burst-count":"creation-particles-per-second"),obj.ParticleCount,v=>obj.ParticleCount=(int)v,0,10000,1);
            Number(L("creation-particle-lifetime"),obj.ParticleLife,v=>obj.ParticleLife=v,.01,300);
            Number(L("creation-particle-life-spread"),settings.LifeSpread,v=>Set(s=>s.LifeSpread=v),0,300);
            Number(L("creation-particle-seed"),settings.Seed,v=>Set(s=>s.Seed=(int)v),0,int.MaxValue,1);
            VectorField(L("creation-particle-spread"),settings.Spread,v=>Set(s=>s.Spread=ClampParticleVector(v,0,1000)),.01f);
        }
        if(LegacySectionVisible("motion")&&Header(L("creation-particle-motion")))
        {
            VectorField(L("creation-particle-velocity"),settings.Velocity,v=>Set(s=>s.Velocity=ClampParticleVector(v,-1000,1000)),.05f);
            VectorField(L("creation-particle-velocity-spread"),settings.VelocitySpread,v=>Set(s=>s.VelocitySpread=ClampParticleVector(v,0,1000)),.05f);
            VectorField(L("creation-particle-acceleration"),settings.Acceleration,v=>Set(s=>s.Acceleration=ClampParticleVector(v,-1000,1000)),.1f);
            Number(L("creation-particle-spin"),settings.Spin,v=>Set(s=>s.Spin=v),-3600,3600,1);
        }
        if(LegacySectionVisible("appearance")&&Header(L("creation-particle-appearance")))
        {
            Choice(L("creation-particle-shape"),settings.Shape,v=>Set(s=>s.Shape=v),[L("creation-particle-soft"),L("creation-particle-quad"),L("creation-particle-cube")]);
            Number(L("creation-particle-size-start"),settings.StartSize,v=>Set(s=>s.StartSize=v),.001,100,.01f);
            Number(L("creation-particle-size-end"),settings.EndSize,v=>Set(s=>s.EndSize=v),0,100,.01f);
            Number(L("creation-particle-size-spread"),settings.SizeSpread,v=>Set(s=>s.SizeSpread=v),0,100,.01f);
            Number(L("creation-particle-alpha-start"),settings.StartOpacity,v=>Set(s=>s.StartOpacity=v),0,1,.01f);
            Number(L("creation-particle-alpha-end"),settings.EndOpacity,v=>Set(s=>s.EndOpacity=v),0,1,.01f);
            void Color(string label,CreationVector value,Action<CreationVector> apply)
            { ImGui.TextWrapped(label);ImGui.SetNextItemWidth(-1);var color=value.Float();bool changed=ImGui.ColorEdit3("##"+label,ref color);Widget(label,changed,()=>apply(CreationVector.From(color))); }
            Color(L("creation-particle-color-start"),obj.Color,v=>obj.Color=v);
            Color(L("creation-particle-color-end"),settings.EndColor,v=>Set(s=>s.EndColor=v));
            Number(L("creation-glow"),obj.Glow,v=>obj.Glow=v,0,1,.01f);
            TextField(L("creation-particle-texture"),settings.Texture,v=>Set(s=>s.Texture=v.Trim()));
            if(Button(L("creation-particle-import-texture"))) FileDialog(L("creation-particle-import-texture"),file=>
            {
                Session.Edit(L("creation-particle-import-texture"),p=>{var asset=Session.Store.Import(p,file,CreationAssetKind.Texture);obj.ParticleSettings??=CreationParticleSettings.Preset(obj.ResourceCode);obj.ParticleSettings.Texture=asset.Source;});
                Runtime.Renderer.ReloadTextures();
            });
        }
        if(!LegacySectionVisible("files"))return;
        if(Button(L("creation-particle-import")))FileDialog(L("creation-particle-import"),file=>
        {
            var preset=JsonSerializer.Deserialize<CreationParticlePreset>(File.ReadAllText(file),CreationJson.Options)??throw new InvalidDataException(L("creation-particle-invalid-preset"));
            preset.Validate();
            Session.Edit(L("creation-particle-import"),p=>
            {
                if(preset.TextureData.Length>0)
                {
                    var asset=new CreationAsset{Kind=CreationAssetKind.Texture,Name=Path.GetFileNameWithoutExtension(file)};
                    asset.Source="assets/"+asset.Id.ToString("N")+preset.TextureExtension;
                    string destination=Session.Store.AssetPath(p.Id,asset.Source);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.WriteAllBytes(destination,preset.TextureData);p.Assets.Add(asset);preset.Settings.Texture=asset.Source;
                }
                else if(preset.Settings.Texture.StartsWith("assets/",StringComparison.Ordinal)&&!File.Exists(Session.Store.AssetPath(p.Id,preset.Settings.Texture)))
                    throw new InvalidDataException(L("creation-particle-invalid-preset"));
                obj.ParticleSettings=preset.Settings;obj.ParticleCount=preset.Rate;obj.ParticleLife=preset.Life;obj.ParticleDuration=preset.Duration;obj.Color=preset.Color;obj.Glow=preset.Glow;
            });
            Runtime.Renderer.ReloadTextures();
        });
        SameLineIfFits(L("creation-particle-export"));
        if(Button(L("creation-particle-export")))FileDialog(L("creation-particle-export"),file=>
        {
            var preset=new CreationParticlePreset{Settings=CreationJson.Clone(obj.ParticleSettings??CreationParticleSettings.Preset(obj.ResourceCode)),Rate=obj.ParticleCount,Life=obj.ParticleLife,Duration=obj.ParticleDuration,Color=obj.Color,Glow=obj.Glow};
            if(preset.Settings.Texture.StartsWith("assets/",StringComparison.Ordinal))
            {preset.TextureData=File.ReadAllBytes(Session.Store.AssetPath(Project!.Id,preset.Settings.Texture));preset.TextureExtension=Path.GetExtension(preset.Settings.Texture).ToLowerInvariant();}
            preset.Validate();DirectorCreationStore.AtomicWrite(file,JsonSerializer.Serialize(preset,CreationJson.Options));
        },"particle.json");
    }
    private static CreationVector ClampParticleVector(CreationVector v,double min,double max)=>new(Math.Clamp(v.X,min,max),Math.Clamp(v.Y,min,max),Math.Clamp(v.Z,min,max));
}
