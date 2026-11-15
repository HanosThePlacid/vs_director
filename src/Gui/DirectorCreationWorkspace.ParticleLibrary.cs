using System.Text.Json;
using System.Numerics;
using ImGuiNET;
using VSDirector.Data;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private string particlePresetSearch="";
    private IReadOnlyList<ParticleLibraryPreset>? particleLibraryCache;
    private DirectorParticlePresetLibrary ParticleLibrary=>new(Session.Store.Root);
    private void DrawParticleLibrary(CreationSceneObject obj)
    {
        ImGui.SeparatorText(P("preset-library"));
        if(Button(P("save-preset")))Ask(P("save-preset"),name=>Run(()=>
        {
            var copy=CreationJson.Clone(obj.ParticleEffect!);foreach(var e in copy.AllNodes())if(e.Texture.StartsWith("assets/",StringComparison.Ordinal))copy.Textures[e.Texture]=File.ReadAllBytes(Session.Store.AssetPath(Project!.Id,e.Texture));
            ParticleLibrary.Save(new(){Name=name,Effect=copy});particleLibraryCache=null;
        }),obj.Name);
        ImGui.SetNextItemWidth(-1);ImGui.InputTextWithHint("##preset-search",P("search-presets"),ref particlePresetSearch,128);
        foreach(string example in DirectorParticlePresetLibrary.Examples)
            if(example.Contains(particlePresetSearch,StringComparison.OrdinalIgnoreCase)&&Button(P("example-"+example)))LoadParticleLibraryEffect(obj,DirectorParticlePresetLibrary.Example(example));
        particleLibraryCache??=ParticleLibrary.List();
        foreach(var preset in particleLibraryCache.Where(p=>(p.Name+" "+p.Tags).Contains(particlePresetSearch,StringComparison.OrdinalIgnoreCase)))
        {
            ImGui.PushID(preset.Id.ToString());
            if(Button((preset.Favorite?"★ ":"")+preset.Name))LoadParticleLibraryEffect(obj,preset.Effect);
            if(ImGui.BeginPopupContextItem("preset-menu"))
            {
                if(ImGui.MenuItem(P("rename")))Ask(P("rename"),v=>Run(()=>{preset.Name=v;ParticleLibrary.Save(preset);particleLibraryCache=null;}),preset.Name);
                if(ImGui.MenuItem(P("tags")))Ask(P("tags"),v=>Run(()=>{preset.Tags=v;ParticleLibrary.Save(preset);particleLibraryCache=null;}),preset.Tags);
                if(ImGui.MenuItem(P("favorite"),"",preset.Favorite))Run(()=>{preset.Favorite=!preset.Favorite;ParticleLibrary.Save(preset);particleLibraryCache=null;});
                if(ImGui.MenuItem(L("creation-duplicate")))Run(()=>{var copy=CreationJson.Clone(preset);copy.Id=Guid.NewGuid();copy.Name+=" (2)";ParticleLibrary.Save(copy);particleLibraryCache=null;});
                if(ImGui.MenuItem(L("creation-delete")))Run(()=>{ParticleLibrary.Delete(preset.Id);particleLibraryCache=null;});
                ImGui.EndPopup();
            }
            ImGui.PopID();
        }
    }
    private void LoadParticleLibraryEffect(CreationSceneObject obj,CreationParticleEffect effect,bool add=false)=>Run(()=>
    {
        if(add&&projectionSchemeTarget!=Guid.Empty){obj.ParticleEffect=effect.Duplicate();if(AcceptProjectionScheme(obj))return;}
        if(add)AttachProjectionParticle(obj);
        var copy=effect.Duplicate();copy.Validate();
        foreach(var source in copy.Textures.Keys)if(Path.GetExtension(source).ToLowerInvariant() is not (".png" or ".jpg" or ".jpeg" or ".bmp"))throw new InvalidDataException("Invalid texture extension.");
        Session.Edit(P("preset-library"),p=>
        {
            foreach(var (source,data) in copy.Textures)
            {
                string extension=Path.GetExtension(source).ToLowerInvariant();if(extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))throw new InvalidDataException("Invalid texture extension.");
                var asset=new CreationAsset{Kind=CreationAssetKind.Texture,Name=Path.GetFileName(source)};asset.Source="assets/"+asset.Id.ToString("N")+extension;
                string path=Session.Store.AssetPath(p.Id,asset.Source);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,data);p.Assets.Add(asset);
                foreach(var node in copy.AllNodes().Where(e=>e.Texture==source))node.Texture=asset.Source;
            }
            copy.Textures.Clear();if(add)p.Objects.Add(obj);obj.ParticleEffect=copy;obj.ParticleDuration=Math.Max(obj.ParticleDuration,copy.Emitters.Select(e=>e.Start+e.Duration).DefaultIfEmpty(0).Max());
            particleEmitter=copy.Emitters.FirstOrDefault()?.Id??Guid.Empty;
        });Runtime.Renderer.ReloadTextures();if(add){Select(obj);auxiliary="";ClearParticlePreviews();}
    });
}
