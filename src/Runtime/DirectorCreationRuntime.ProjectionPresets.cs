using System.Text.Json;
using VSDirector.Data;

namespace VSDirector.Runtime;

internal sealed partial class DirectorCreationRuntime
{
    private sealed class ProjectionPreset
    {
        public int Version {get;set;}=2;
        public Guid RootId {get;set;}
        public DirectorCreationProject? Scene {get;set;}
        public Dictionary<string,byte[]> Files {get;set;}=[];
        public CreationSceneObject Root {get;set;}=new();
        public List<CreationSceneObject> Children {get;set;}=[];
        public List<CreationTrack> Tracks {get;set;}=[];
        public string BlueprintExtension {get;set;}=".vsbp";
        public byte[] Blueprint {get;set;}=[];
        public Dictionary<string,byte[]> Textures {get;set;}=[];
    }
    internal void ExportProjectionPreset(CreationSceneObject obj,string file)
    {
        var project=Session.Project!;var scene=project.Clone();
        var ids=new HashSet<Guid>{obj.Id};bool added;
        do{added=false;foreach(var child in project.Objects.Where(o=>ids.Contains(o.ParentId)))added|=ids.Add(child.Id);}while(added);
        var ancestor=obj.ParentId;while(ancestor!=Guid.Empty&&project.Objects.Find(o=>o.Id==ancestor) is {} parent){ids.Add(ancestor);ancestor=parent.ParentId;}
        scene.Objects.RemoveAll(o=>!ids.Contains(o.Id));scene.Tracks.RemoveAll(t=>!ids.Contains(t.ObjectId));scene.Clips.RemoveAll(c=>!ids.Contains(c.ObjectId));
        scene.Animations.RemoveAll(a=>!scene.Clips.Any(c=>c.AnimationId==a.Id));scene.Models.RemoveAll(m=>!scene.Objects.Any(o=>o.ModelId==m.Id));
        scene.Presets.Clear();scene.CameraId=Guid.Empty;scene.SetBackground(Guid.Empty);scene.Performances.Clear();
        foreach(var item in scene.Objects)
        {
            if(item.TargetId!=Guid.Empty&&!ids.Contains(item.TargetId))throw new InvalidDataException(item.Name+": include the target object in the structure hierarchy before exporting.");
            foreach(var node in DirectorProjectionEditing.Effects(item).SelectMany(e=>e.AllNodes()))
                if(node.Modules is {} m&&(m.BindObject!=Guid.Empty&&!ids.Contains(m.BindObject)||m.EndObject!=Guid.Empty&&!ids.Contains(m.EndObject)))
                    throw new InvalidDataException(item.Name+": include particle binding targets in the structure hierarchy before exporting.");
        }
        if(scene.Clips.Any(c=>c.PerformanceAssetId!=Guid.Empty))throw new InvalidDataException("Bake performance clips before exporting a structure preset.");
        var needed=DirectorCreationDependencies.Collect(scene).Where(d=>d.Source.StartsWith("assets/",StringComparison.Ordinal)).Select(d=>d.Source).ToHashSet();
        scene.Assets.RemoveAll(a=>!needed.Contains(a.Source));
        var preset=new ProjectionPreset{RootId=obj.Id,Scene=scene};
        foreach(string source in needed)preset.Files[source]=File.ReadAllBytes(Session.Store.AssetPath(project.Id,source));
        scene.Validate();DirectorCreationStore.AtomicWrite(file,JsonSerializer.Serialize(preset,CreationJson.Options));
    }
    internal CreationSceneObject ImportProjectionPreset(string file)
    {
        var preset=JsonSerializer.Deserialize<ProjectionPreset>(File.ReadAllText(file),CreationJson.Options)??throw new InvalidDataException("Invalid structure preset.");
        if(preset.Version==2)return ImportProjectionScene(preset,CursorPosition());
        if(preset.Version!=1||preset.Root.Projection is null||preset.BlueprintExtension is not (".json" or ".vsbp"))throw new InvalidDataException("Unsupported structure preset.");
        preset.Root.Projection.Validate();var objects=preset.Children.Prepend(preset.Root).ToArray();var ids=objects.ToDictionary(o=>o.Id,_=>Guid.NewGuid());
        foreach(var obj in objects){obj.Id=ids[obj.Id];obj.ParentId=ids.GetValueOrDefault(obj.ParentId);obj.TargetId=ids.GetValueOrDefault(obj.TargetId);}
        preset.Root.Transform.Position=CursorPosition();
        string temp=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+preset.BlueprintExtension);
        try
        {
            File.WriteAllBytes(temp,preset.Blueprint);VSDirector.Blueprints.DirectorBlueprintService.Validate(new VSDirector.Blueprints.BlueprintCodec().Read(temp));
            Session.Edit("Import structure preset",project=>
            {
                preset.Root.AssetId=Session.Store.Import(project,temp,CreationAssetKind.Structure).Id;
                var effects=objects.Where(o=>o.ParticleEffect is not null).Select(o=>o.ParticleEffect!).Concat(preset.Root.Projection.ParticleSchemes.Values).ToArray();
                foreach(var (old,data) in preset.Textures)
                {
                    string extension=Path.GetExtension(old);if(extension is not (".png" or ".jpg" or ".jpeg" or ".bmp"))throw new InvalidDataException("Invalid texture extension.");
                    var asset=new CreationAsset{Kind=CreationAssetKind.Texture,Name=Path.GetFileName(old)};asset.Source="assets/"+asset.Id.ToString("N")+extension;
                    var path=Session.Store.AssetPath(project.Id,asset.Source);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,data);project.Assets.Add(asset);
                    foreach(var e in effects.SelectMany(e=>e.AllNodes()).Where(e=>e.Texture==old))e.Texture=asset.Source;
                }
                project.Objects.AddRange(objects);
                foreach(var track in preset.Tracks){track.Id=Guid.NewGuid();track.ObjectId=ids[track.ObjectId];foreach(var key in track.Keys)key.Id=Guid.NewGuid();project.Tracks.Add(track);}
            });return preset.Root;
        }
        finally{File.Delete(temp);}
    }
    private CreationSceneObject ImportProjectionScene(ProjectionPreset preset,CreationVector position)
    {
        var source=preset.Scene??throw new InvalidDataException("Missing structure scene.");source.Validate();
        int rootIndex=source.Objects.FindIndex(o=>o.Id==preset.RootId&&o.Projection is not null);
        if(rootIndex<0)throw new InvalidDataException("Missing structure root.");
        if(source.Clips.Any(c=>c.PerformanceAssetId!=Guid.Empty))throw new InvalidDataException("Bake performance clips before exporting a structure preset.");
        var scene=DirectorCreationEdits.Duplicate(source);var root=scene.Objects[rootIndex];
        var renamed=new Dictionary<string,string>(StringComparer.Ordinal);
        foreach(var (old,data) in preset.Files)
        {
            if(!old.StartsWith("assets/",StringComparison.Ordinal)||data is null)throw new InvalidDataException("Invalid structure resource.");
            string extension=Path.GetExtension(old).ToLowerInvariant();
            if(extension is not (".png" or ".jpg" or ".jpeg" or ".bmp" or ".json" or ".vsbp" or ".ogg" or ".wav" or ".mp3"))throw new InvalidDataException("Unsupported structure resource: "+old);
            renamed[old]="assets/"+Guid.NewGuid().ToString("N")+extension;
        }
        foreach(var dependency in DirectorCreationDependencies.Collect(source).Where(d=>d.Source.StartsWith("assets/",StringComparison.Ordinal)))
            if(!preset.Files.ContainsKey(dependency.Source))throw new InvalidDataException("Missing structure resource: "+dependency.Source);
        foreach(var asset in scene.Assets)if(renamed.TryGetValue(asset.Source,out var path))asset.Source=path;
        foreach(var model in scene.Models)foreach(var key in model.Textures.Keys.ToArray())if(renamed.TryGetValue(model.Textures[key],out var path))model.Textures[key]=path;
        foreach(var item in scene.Objects)
        {
            foreach(var node in DirectorProjectionEditing.Effects(item).SelectMany(e=>e.AllNodes()))if(renamed.TryGetValue(node.Texture,out var path))node.Texture=path;
            if(item.ParticleSettings is {} legacy&&renamed.TryGetValue(legacy.Texture,out var texture))legacy.Texture=texture;
        }
        // A placement group translates the whole authored hierarchy without changing its animated tracks.
        var current=DirectorCreationEvaluator.Evaluate(scene,0).Objects[root.Id].World.Translation;
        var placement=new CreationSceneObject{Kind=CreationObjectKind.Group,Name=root.Name+" / placement",Transform=new(){Position=position-CreationVector.From(current)}};
        foreach(var item in scene.Objects.Where(o=>o.ParentId==Guid.Empty))item.ParentId=placement.Id;
        scene.Objects.Add(placement);scene.Validate();
        Session.Edit("Import structure preset",project=>
        {
            foreach(var (old,data) in preset.Files)
            {
                string path=Session.Store.AssetPath(project.Id,renamed[old]);Directory.CreateDirectory(Path.GetDirectoryName(path)!);File.WriteAllBytes(path,data);
                if(scene.Assets.Any(a=>a.Source==renamed[old]&&a.Kind==CreationAssetKind.Structure))VSDirector.Blueprints.DirectorBlueprintService.Validate(new VSDirector.Blueprints.BlueprintCodec().Read(path));
            }
            project.Assets.AddRange(scene.Assets);project.Models.AddRange(scene.Models);project.Objects.AddRange(scene.Objects);
            project.Tracks.AddRange(scene.Tracks);project.Animations.AddRange(scene.Animations);project.Clips.AddRange(scene.Clips);
        });return root;
    }

}
