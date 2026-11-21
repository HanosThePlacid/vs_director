using System.Text.Json;

namespace VSDirector.Data;

public sealed class ParticleLibraryPreset
{
    public Guid Id {get;set;}=Guid.NewGuid();
    public string Name {get;set;}="Effect";
    public string Tags {get;set;}="";
    public bool Favorite {get;set;}
    public CreationParticleEffect Effect {get;set;}=CreationParticleEffect.Create();
}
public sealed class DirectorParticlePresetLibrary(string root)
{
    public string DirectoryPath {get;}=Path.Combine(Path.GetFullPath(root),"particle-presets");
    private string PathFor(Guid id)=>Path.Combine(DirectoryPath,id.ToString("N")+".json");
    public IReadOnlyList<ParticleLibraryPreset> List()
    {
        if(!Directory.Exists(DirectoryPath))return [];
        var result=new List<ParticleLibraryPreset>();
        foreach(string path in Directory.EnumerateFiles(DirectoryPath,"*.json"))
        {
            if(!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path),"N",out var fileId))continue;
            try{if(new FileInfo(path).Length>64*1024*1024)continue;var preset=JsonSerializer.Deserialize<ParticleLibraryPreset>(File.ReadAllText(path),CreationJson.Options);if(preset is null||preset.Id!=fileId||preset.Name is null||preset.Tags is null||preset.Effect is null)continue;preset.Effect.Validate();result.Add(preset);}
            catch(Exception e)when(e is IOException or JsonException or InvalidDataException){ }
        }
        return result.OrderByDescending(p=>p.Favorite).ThenBy(p=>p.Name,StringComparer.OrdinalIgnoreCase).ToArray();
    }
    public void Save(ParticleLibraryPreset preset)
    {if(preset.Id==Guid.Empty||preset.Name is null||preset.Tags is null||preset.Effect is null)throw new InvalidDataException("Invalid particle preset metadata.");preset.Effect.Validate();Directory.CreateDirectory(DirectoryPath);DirectorCreationStore.AtomicWrite(PathFor(preset.Id),JsonSerializer.Serialize(preset,CreationJson.Options));}
    public void Delete(Guid id)=>File.Delete(PathFor(id));
    public static readonly string[] Examples=["fire","smoke","steam","sparks","debris","dust","snow","drips","bubbles","trail","beam","lightning"];
    public static CreationParticleEffect Example(string code)
    {
        var effect=CreationParticleEffect.Create();effect.Name=code;var e=effect.Emitters[0];e.Name=code;e.Duration=5;
        e.Properties["GravityEffect"]=CreationParticleFields.Nat(0);e.Properties["TerrainCollision"]=false;
        e.Properties["Velocity"]=new System.Text.Json.Nodes.JsonArray(CreationParticleFields.Nat(0,.15),CreationParticleFields.Nat(.5,.1),CreationParticleFields.Nat(0,.15));
        e.Properties["Size"]=CreationParticleFields.Nat(.4,.1);e.Properties["LifeLength"]=CreationParticleFields.Nat(.3,.1);
        e.Properties["OpacityEvolve"]=CreationParticleFields.Evolution("LINEAR",-255);
        switch(code)
        {
            case "fire":case "sparks":e.Additive=true;e.Properties["HsvaColor"]=new System.Text.Json.Nodes.JsonArray(CreationParticleFields.Nat(25,15),CreationParticleFields.Nat(255),CreationParticleFields.Nat(255),CreationParticleFields.Nat(255));e.Properties["VertexFlags"]=180;e.Properties["SizeEvolve"]=CreationParticleFields.Evolution("LINEAR",-.4);if(code=="sparks"){e.Properties["Size"]=CreationParticleFields.Nat(.08);e.Properties["GravityEffect"]=CreationParticleFields.Nat(.1);e.Properties["TerrainCollision"]=true;e.Properties["Bounciness"]=.8;}break;
            case "smoke":case "steam":e.Properties["SizeEvolve"]=CreationParticleFields.Evolution("LINEAR",3);e.Properties["HsvaColor"]=new System.Text.Json.Nodes.JsonArray(CreationParticleFields.Nat(0),CreationParticleFields.Nat(0),CreationParticleFields.Nat(code=="smoke"?75:220),CreationParticleFields.Nat(90));e.Properties["WindAffectednes"]=.5;break;
            case "debris":e.Burst=true;e.Properties["Quantity"]=CreationParticleFields.Nat(20);e.Properties["ParticleModel"]="Cube";e.Properties["GravityEffect"]=CreationParticleFields.Nat(.3);e.Properties["TerrainCollision"]=true;e.Properties["Bounciness"]=.5;break;
            case "dust":e.Modules!.Shape=ParticleSpawnShape.Box;e.Modules.Extent=new(2,.1,2);e.Properties["WindAffectednes"]=.2;break;
            case "snow":e.Modules!.Shape=ParticleSpawnShape.Box;e.Modules.Extent=new(4,.1,4);e.Offset=new(0,3,0);e.Properties["GravityEffect"]=CreationParticleFields.Nat(.02);e.Properties["WindAffectednes"]=.5;break;
            case "drips":e.Properties["GravityEffect"]=CreationParticleFields.Nat(.3);e.Properties["Size"]=CreationParticleFields.Nat(.08);e.Properties["DieInLiquid"]=true;break;
            case "bubbles":e.Properties["DieInAir"]=true;e.Properties["Size"]=CreationParticleFields.Nat(.12);break;
            case "trail":e.Modules!.Geometry=ParticleGeometry.Trail;e.Additive=true;break;
            case "beam":e.Modules!.Geometry=ParticleGeometry.Beam;e.Additive=true;break;
            case "lightning":e.Modules!.Geometry=ParticleGeometry.Lightning;e.Additive=true;break;
            default:throw new InvalidDataException("Unknown particle example.");
        }
        effect.Validate();return effect;
    }
}
