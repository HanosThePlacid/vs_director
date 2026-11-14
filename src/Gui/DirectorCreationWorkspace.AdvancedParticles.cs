using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ImGuiNET;
using VSDirector.Data;
using VSDirector.Runtime;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private Guid particleEmitter;
    private string particleSearch="",particleJson="",particleJsonId="";
    private int particleScope;
    private string particleResourceId="";
    private void DrawAdvancedParticleEditor(CreationSceneObject obj)
    {
        var effect=obj.ParticleEffect!;
        if(Button(L(Session.Playing?"creation-pause":"creation-particle-play")))
        {if(Session.Playing)Session.Play(false);else{Runtime.Seek(obj.ParticleStart);Session.Play(true);}}
        SameLineIfFits(L("creation-particle-reset"));
        if(Button(L("creation-particle-reset"))){Session.Play(false);Runtime.Seek(obj.ParticleStart);}
        if(Runtime.ParticleRebuildCancelled)Hint(L("creation-particle-cancelled"));
        else if(!Runtime.ParticlesReady)
        {
            ImGui.ProgressBar((float)Runtime.ParticleProgress);Hint(L("creation-particle-rebuilding"));
            if(Button(L("creation-particle-cancel")))Runtime.CancelParticleRebuild();
        }
        if(Runtime.ParticleError.Length>0)ImGui.TextWrapped(Runtime.ParticleError);
        if(Button(L("creation-particle-refresh-environment")))Runtime.InvalidateParticleEnvironment();
        TextField(L("creation-particle-effect-name"),effect.Name,v=>effect.Name=v);
        TimeNumber(L("creation-start-time"),obj.ParticleStart,v=>obj.ParticleStart=v,0,86400);
        TimeNumber(L("creation-emission-duration"),obj.ParticleDuration,v=>obj.ParticleDuration=v,0,86400);
        Number(L("creation-particle-seed"),effect.Seed,v=>effect.Seed=(int)v,0,int.MaxValue,1);
        var emitter=effect.Emitters.FirstOrDefault(e=>e.Id==particleEmitter)??effect.Emitters.FirstOrDefault();
        if(Combo(L("creation-particle-emitter"),emitter?.Name??""))
        {foreach(var e in effect.Emitters)if(ImGui.Selectable(e.Name+"##"+e.Id,e==emitter))particleEmitter=e.Id;ImGui.EndCombo();}
        if(Button(L("creation-particle-add-emitter")))Session.Edit(L("creation-particle-add-emitter"),_=>{var e=new CreationParticleEmitter();effect.Emitters.Add(e);particleEmitter=e.Id;});
        if(emitter is not null)
        {
            SameLineIfFits(L("creation-duplicate"));
            if(Button(L("creation-duplicate")))Session.Edit(L("creation-duplicate"),_=>{var copy=CreationJson.Clone(emitter);copy.Id=Guid.NewGuid();copy.Name+=" (2)";effect.Emitters.Add(copy);particleEmitter=copy.Id;});
            SameLineIfFits(L("creation-delete"));
            if(Button(L("creation-delete"))){Session.Edit(L("creation-delete"),_=>effect.Emitters.Remove(emitter));return;}
            TextField(L("creation-name"),emitter.Name,v=>emitter.Name=v);
            Toggle(L("creation-enabled"),emitter.Enabled,v=>emitter.Enabled=v);
            if(emitter.Legacy is not null)Hint(L("creation-particle-converted-hint"));
            Hint(emitter.Source);
            string[] scopes=[L("creation-particle-scope-emitter"),L("creation-particle-scope-variant"),L("creation-particle-scope-family")];
            if(Combo(L("creation-particle-edit-scope"),scopes[particleScope]))
            {for(int i=0;i<scopes.Length;i++)if(ImGui.Selectable(scopes[i],i==particleScope))particleScope=i;ImGui.EndCombo();}
            if(particleScope>0)Hint(L("creation-particle-batch-warning"));
            if(ImGui.BeginTabBar("particle-tabs"))
            {
                if(ImGui.BeginTabItem(L("creation-particle-parameters")))
                {
                    if(Header(L("creation-particle-emission")))
                    {
                        VectorField(L("creation-particle-offset"),emitter.Offset,v=>emitter.Offset=v,.01f);
                        TimeNumber(L("creation-start-time"),emitter.Start,v=>emitter.Start=v,0,86400);
                        Number(L("creation-particle-interval"),emitter.Interval,v=>emitter.Interval=v,.001,86400,.01f);
                        TimeNumber(L("creation-emission-duration"),emitter.Duration,v=>emitter.Duration=v,0,86400);
                        Toggle(L("creation-particle-burst"),emitter.Burst,v=>emitter.Burst=v);
                        Toggle(L("creation-particle-local-follow"),emitter.LocalFollow,v=>emitter.LocalFollow=v);
                    }
                    DrawNativeParticleFields(effect,emitter,emitter.Properties,[],0);
                    if(Header(L("creation-particle-rendering")))
                    {
                        TextField(L("creation-particle-texture"),emitter.Texture,v=>emitter.Texture=v.Trim());
                        if(Button(L("creation-particle-import-texture")))FileDialog(L("creation-particle-import-texture"),file=>
                        {Session.Edit(L("creation-particle-import-texture"),p=>emitter.Texture=Session.Store.Import(p,file,CreationAssetKind.Texture).Source);Runtime.Renderer.ReloadTextures();});
                        Toggle(L("creation-particle-soft"),emitter.SoftEdge,v=>emitter.SoftEdge=v);
                        Toggle(L("creation-particle-additive"),emitter.Additive,v=>emitter.Additive=v);
                        Number(L("creation-particle-parent-velocity"),emitter.ParentVelocityWeight,v=>emitter.ParentVelocityWeight=v,0,1000,.01f);
                    }
                    ImGui.EndTabItem();
                }
                if(ImGui.BeginTabItem("JSON"))
                {
                    string id=obj.Id+"/"+emitter.Id;
                    if(particleJsonId!=id){particleJsonId=id;particleJson=emitter.Properties.ToJsonString(CreationJson.Options);}
                    if(Button(L("creation-particle-json-refresh")))particleJson=emitter.Properties.ToJsonString(CreationJson.Options);
                    ImGui.InputTextMultiline("##particle-json",ref particleJson,1024*1024,new(-1,260));
                    if(Button(L("creation-apply")))Run(()=>
                    {var p=JsonNode.Parse(particleJson) as JsonObject??throw new InvalidDataException("Expected particle object.");CreationParticleFields.Validate(p);Session.Edit(L("creation-apply"),_=>SetParticleNode(effect,emitter,[],p));});
                    ImGui.EndTabItem();
                }
                ImGui.EndTabBar();
            }
            foreach(string warning in CreationParticleFields.Warnings(emitter.Properties))Hint(warning);
            if(Button(L("creation-particle-restore-original")))Session.Edit(L("creation-particle-restore-original"),_=>
            {emitter.Properties=(JsonObject)(emitter.Original??CreationParticleFields.Defaults()).DeepClone();emitter.Legacy=null;});
            Runtime.ParticleResources.Start();
            var source=Runtime.ParticleResources.Entries.FirstOrDefault(r=>r.Id==emitter.Source);
            if(source is not null)
            {
                IEnumerable<DirectorParticleResource> Targets()=>Runtime.ParticleResources.Entries.Where(r=>
                    particleScope==0?r.Id==source.Id: r.Index==source.Index && (particleScope==1?r.Family==source.Family&&r.Variant==source.Variant:r.Family==source.Family));
                if(Button(L("creation-particle-apply-client")))Run(()=>Runtime.ParticleOverrides.Apply(Targets(),obj,emitter));
                SameLineIfFits(L("creation-particle-restore-client"));
                if(Button(L("creation-particle-restore-client")))Run(()=>Runtime.ParticleOverrides.Restore(Targets()));
            }
        }
        if(Runtime.ParticleOverrides.Count>0 && Button(L("creation-particle-restore-all")))Run(()=>Runtime.ParticleOverrides.Restore());
        if(Button(L("creation-particle-import")))FileDialog(L("creation-particle-import"),file=>
        {
            var imported=CreationParticleFields.Import(File.ReadAllText(file));
            Session.Edit(L("creation-particle-import"),p=>
            {
                foreach(var pair in imported.Textures)
                {
                    string extension=Path.GetExtension(pair.Key).ToLowerInvariant();
                    if(extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))throw new InvalidDataException("Unsupported texture.");
                    var asset=new CreationAsset{Kind=CreationAssetKind.Texture,Name=Path.GetFileName(pair.Key)};
                    asset.Source="assets/"+asset.Id.ToString("N")+extension;
                    string destination=Session.Store.AssetPath(p.Id,asset.Source);Directory.CreateDirectory(Path.GetDirectoryName(destination)!);File.WriteAllBytes(destination,pair.Value);p.Assets.Add(asset);
                    foreach(var e in imported.Emitters.Where(e=>e.Texture==pair.Key))e.Texture=asset.Source;
                }
                imported.Textures.Clear();obj.ParticleEffect=imported;
                obj.ParticleDuration=Math.Max(obj.ParticleDuration,imported.Emitters.Select(e=>e.Start+e.Duration).DefaultIfEmpty(0).Max());
            });Runtime.Renderer.ReloadTextures();
        });
        SameLineIfFits(L("creation-particle-export"));
        if(Button(L("creation-particle-export")))FileDialog(L("creation-particle-export"),file=>
        {
            var copy=CreationJson.Clone(effect);
            foreach(var e in copy.Emitters)if(e.Texture.StartsWith("assets/",StringComparison.Ordinal))copy.Textures[e.Texture]=File.ReadAllBytes(Session.Store.AssetPath(Project!.Id,e.Texture));
            copy.Validate();DirectorCreationStore.AtomicWrite(file,JsonSerializer.Serialize(copy,CreationJson.Options));
        },"particle.director.json");
        if(Button(L("creation-particle-export-native")))FileDialog(L("creation-particle-export-native"),file=>DirectorCreationStore.AtomicWrite(file,CreationParticleFields.ExportNative(effect)),"particle.native.json");
        Hint(L("creation-particle-native-export-hint"));
    }
    private IEnumerable<CreationParticleEmitter> ParticleTargets(CreationParticleEffect effect,CreationParticleEmitter emitter)
        =>effect.Emitters.Where(e=>e==emitter||particleScope>0&&emitter.Source.Length>0&&e.Family==emitter.Family
            &&(particleScope==2||e.Variant==emitter.Variant));
    private static JsonNode? ParticleNodeAt(JsonObject root,IReadOnlyList<string> path)
    {
        JsonNode? node=root;foreach(string key in path)node=node is JsonObject o?CreationParticleFields.Find(o,key):node is JsonArray a&&int.TryParse(key,out int i)&&i<a.Count?a[i]:null;return node;
    }
    private void SetParticleNode(CreationParticleEffect effect,CreationParticleEmitter emitter,string[] path,JsonNode? value)
    {
        foreach(var target in ParticleTargets(effect,emitter))
        {
            if(path.Length==0){target.Properties=(JsonObject)value!.DeepClone();target.Legacy=null;continue;}
            JsonNode? parent=ParticleNodeAt(target.Properties,path[..^1]);
            if(parent is JsonObject o)CreationParticleFields.Set(o,path[^1],value?.DeepClone());
            else if(parent is JsonArray a&&int.TryParse(path[^1],out int index)&&index<a.Count)a[index]=value?.DeepClone();
            else continue;
            target.Legacy=null;
        }
    }
    private void DrawNativeParticleFields(CreationParticleEffect effect,CreationParticleEmitter emitter,JsonObject properties,string[] path,int depth)
    {
        foreach(string group in new[]{"emission","motion","appearance","environment","children"})
        {
            ImGui.PushID(group);
            if(Header(L("creation-particle-"+group)))foreach(var field in CreationParticleFields.All.Where(f=>f.Group==group))
            {
                ImGui.PushID(field.Name);var node=CreationParticleFields.Find(properties,field.Name);
                string label=L("creation-particle-field-"+field.Name.ToLowerInvariant());var fieldPath=path.Append(field.Name).ToArray();
                void Set(JsonNode? n)=>SetParticleNode(effect,emitter,fieldPath,n);
                if(field.Kind=="bool")Toggle(label,node?.GetValue<bool>()??field.Default!=0,v=>Set(JsonValue.Create(v)));
                else if(field.Kind is "number" or "int")Number(label,CreationParticleFields.Number(properties,field.Name,field.Default),v=>Set(JsonValue.Create(field.Kind=="int"?(int)v:v)),-int.MaxValue,int.MaxValue,field.Kind=="int"?1:.01f);
                else if(field.Kind=="model")
                {
                    if(Combo(label,node?.ToString()??"Cube")){foreach(string m in new[]{"Cube","Quad"})if(ImGui.Selectable(m))Session.Edit(label,_=>Set(JsonValue.Create(m)));ImGui.EndCombo();}
                }
                else if(field.Kind=="children")
                {
                    ImGui.TextWrapped(label);
                    var children=node as JsonArray??new JsonArray();
                    for(int i=0;i<children.Count;i++)
                    {
                        ImGui.PushID(i);int index=i;
                        if(children[i] is JsonObject child && Header(label+" "+(i+1)))
                        {
                            if(Button(L("creation-duplicate")))Session.Edit(label,_=>{var copy=(JsonArray)children.DeepClone();copy.Add(child.DeepClone());Set(copy);});
                            SameLineIfFits(L("creation-delete"));
                            if(Button(L("creation-delete")))Session.Edit(label,_=>{var copy=(JsonArray)children.DeepClone();copy.RemoveAt(index);Set(copy);});
                            if(depth<8)DrawNativeParticleFields(effect,emitter,child,fieldPath.Append(i.ToString()).ToArray(),depth+1);
                        }
                        ImGui.PopID();
                    }
                    if(depth<8&&Button(L("creation-particle-add-child")))Session.Edit(label,_=>{var copy=(JsonArray)children.DeepClone();copy.Add(CreationParticleFields.Defaults());Set(copy);});
                }
                else
                {
                    ImGui.TextWrapped(label);
                    bool enabled=node is not null && (node is not JsonArray array||array.Any(n=>n is not null));
                    if(field.Kind=="evolve")
                    {
                        if(Checkbox(L("creation-enabled"),ref enabled))Session.Edit(label,_=>Set(enabled?(field.Length>0?new JsonArray(Enumerable.Range(0,field.Length).Select(_=>(JsonNode)CreationParticleFields.Evolution()).ToArray()):CreationParticleFields.Evolution()):null));
                        if(!enabled){ImGui.PopID();continue;}
                    }
                    var nodes=field.Length>0?node as JsonArray:null;
                    if(field.Kind=="nat"&&field.Length==3)
                    {
                        CreationVector Read(string key)=>new(Get(0,key),Get(1,key),Get(2,key));
                        double Get(int i,string key)=>nodes?[i] is JsonObject n?CreationParticleFields.Number(n,key):key=="avg"?field.Default:0;
                        void Values(string key,CreationVector v)
                        {var a=nodes is null?new JsonArray(Enumerable.Range(0,3).Select(_=>(JsonNode)CreationParticleFields.Nat(field.Default)).ToArray()):(JsonArray)nodes.DeepClone();double[] values=[v.X,v.Y,v.Z];for(int i=0;i<3;i++)CreationParticleFields.Set((JsonObject)a[i]!,key,JsonValue.Create(values[i]));Set(a);}
                        VectorField(L("creation-particle-mean"),Read("avg"),v=>Values("avg",v),.01f);
                        VectorField(L("creation-particle-variation"),Read("var"),v=>Values("var",v),.01f);
                    }
                    for(int i=0;i<Math.Max(1,field.Length);i++)
                    {
                        ImGui.PushID(i);int index=i;
                        var n=(field.Length>0?nodes?[i]:node) as JsonObject ?? (field.Kind=="nat"?CreationParticleFields.Nat(field.Default):CreationParticleFields.Evolution());
                        void Part(string key,JsonNode? v)
                        {var copy=(JsonObject)n.DeepClone();CreationParticleFields.Set(copy,key,v);if(field.Length==0)Set(copy);else{var a=nodes is null?new JsonArray(Enumerable.Range(0,field.Length).Select(_=>(JsonNode)(field.Kind=="nat"?CreationParticleFields.Nat(field.Default):CreationParticleFields.Evolution())).ToArray()):(JsonArray)nodes.DeepClone();a[index]=copy;Set(a);}}
                        string suffix=field.Length==0?"":field.Name=="HsvaColor"?new[]{"H","S","V","A"}[i]:new[]{"X","Y","Z"}[i];
                        if(field.Kind=="nat" && field.Length!=3)
                        {Number(suffix+" "+L("creation-particle-mean"),CreationParticleFields.Number(n,"avg",field.Default),v=>Part("avg",JsonValue.Create(v)));Number(suffix+" "+L("creation-particle-variation"),CreationParticleFields.Number(n,"var"),v=>Part("var",JsonValue.Create(v)),0,1e6);}
                        if(field.Kind=="nat"&&CreationParticleFields.Find(n,"offset") is not null)Number(suffix+" offset",CreationParticleFields.Number(n,"offset"),v=>Part("offset",JsonValue.Create(v)));
                        string key=field.Kind=="nat"?"dist":"transform";string[] choices=field.Kind=="nat"?CreationParticleFields.Distributions:CreationParticleFields.Transforms;
                        if(Combo(suffix+" "+L("creation-particle-"+key),CreationParticleFields.Find(n,key)?.ToString()??choices[0]))
                        {foreach(string choice in choices)if(ImGui.Selectable(choice))Session.Edit(label,_=>Part(key,JsonValue.Create(choice)));ImGui.EndCombo();}
                        if(field.Kind=="evolve")
                        {
                            Number(L("creation-particle-factor"),CreationParticleFields.Number(n,"factor"),v=>Part("factor",JsonValue.Create(v)));
                            bool limited=CreationParticleFields.Find(n,"maxvalue") is not null;
                            if(Checkbox(L("creation-particle-maxvalue"),ref limited))Session.Edit(label,_=>Part("maxvalue",limited?JsonValue.Create(255):null));
                            if(limited)Number(L("creation-particle-maxvalue")+"##value",CreationParticleFields.Number(n,"maxvalue"),v=>Part("maxvalue",JsonValue.Create(v)));
                        }
                        ImGui.PopID();
                    }
                }
                ImGui.PopID();
            }
            ImGui.PopID();
        }
    }
}
