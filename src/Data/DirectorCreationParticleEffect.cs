using System.Text.Json;
using System.Text.Json.Nodes;

namespace VSDirector.Data;

public sealed class CreationParticleEffect
{
    public int Version { get; set; } = 2;
    public string Name { get; set; } = "Effect";
    public int Seed { get; set; } = 1;
    public double GameSpeed {get;set;}=1;
    public double Density {get;set;}=1;
    public List<CreationParticleEmitter> Emitters { get; set; } = [new()];
    public Dictionary<string, byte[]> Textures { get; set; } = new();
    public void Validate()
    {
        if (!double.IsFinite(GameSpeed)||GameSpeed is <.05 or >200||!double.IsFinite(Density)||Density is <0 or >100||Version is not (2 or 3) || Name is null || Emitters is null || Emitters.Count > 256 || Textures is null
            || Textures.Any(t => t.Value is null || t.Value.Length > 64 * 1024 * 1024))
            throw new InvalidDataException("Invalid particle effect.");
        var ids = new HashSet<Guid>();
        void Check(CreationParticleEmitter emitter,int depth)
        {
            if(emitter is null || depth>8 || !ids.Add(emitter.Id) || ids.Count>2048)throw new InvalidDataException("Invalid particle hierarchy (depth, duplicate ID or node budget).");
            emitter.Validate();
            if(Version==3 && emitter.Modules is null)throw new InvalidDataException("Version 3 requires particle modules.");
            if(Version==2 && (emitter.Modules is not null || emitter.Children.Count>0))throw new InvalidDataException("Upgrade this effect before adding modules.");
            if(depth>0&&emitter.Modules?.Geometry is ParticleGeometry.Trail or ParticleGeometry.Beam or ParticleGeometry.Lightning)
                throw new InvalidDataException("Geometry emitters must be root nodes. Use their collision children for impact particles.");
            foreach(var child in emitter.Children)Check(child,depth+1);
        }
        foreach (var emitter in Emitters) Check(emitter,0);
    }
    public IEnumerable<CreationParticleEmitter> AllNodes() => Emitters.SelectMany(e=>e.Descendants());
    public CreationParticleEffect Duplicate()
    {
        var copy=CreationJson.Clone(this);var ids=copy.AllNodes().ToDictionary(n=>n.Id,_=>Guid.NewGuid());
        foreach(var node in copy.AllNodes()){node.Id=ids[node.Id];if(node.Modules is {} m&&ids.TryGetValue(m.TrailEmitter,out var source))m.TrailEmitter=source;}
        return copy;
    }
    public static CreationParticleEffect Create() => new() { Version=3,Emitters=[new(){Modules=new()}] };
    public void Upgrade()
    {
        if(Version==3)return;
        foreach(var emitter in Emitters)emitter.Upgrade();
        Version=3;
    }
}

public enum CreationParticleRecipe { None, ReferenceFire }

public sealed class CreationParticleEmitter
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Emitter";
    public string Source { get; set; } = "";
    public string Family { get; set; } = "";
    public string Variant { get; set; } = "";
    public bool Enabled { get; set; } = true;
    public CreationVector Offset { get; set; }
    public double Start { get; set; }
    public double Interval { get; set; } = .1;
    public double Duration { get; set; } = 2;
    public bool Burst { get; set; }
    public bool LocalFollow { get; set; }
    public bool SoftEdge { get; set; } = true;
    public bool Additive { get; set; }
    public string Texture { get; set; } = "";
    public double ParentVelocityWeight { get; set; }
    public JsonObject Properties { get; set; } = CreationParticleFields.Defaults();
    public JsonObject? Original { get; set; }
    // Explicit conversions retain the old evaluator, including its noise and timing.
    public CreationSceneObject? Legacy { get; set; }
    public List<CreationParticleEmitter> Children { get; set; } = [];
    public ParticleTrigger Trigger { get; set; }
    public CreationParticleModules? Modules { get; set; }
    public string Preparation { get; set; } = "";
    public CreationParticleRecipe Recipe {get;set;}
    public int ProviderIndex {get;set;}
    public int ProviderCount {get;set;}
    public IEnumerable<CreationParticleEmitter> Descendants() => new[]{this}.Concat(Children.SelectMany(c=>c.Descendants()));
    public void Upgrade()
    {
        Original ??= (JsonObject)Properties.DeepClone();
        foreach(var (name,trigger) in new[]{("SecondaryParticles",ParticleTrigger.Periodic),("DeathParticles",ParticleTrigger.Death)})
        {
            if(CreationParticleFields.Find(Properties,name) is JsonArray array)
                foreach(var node in array.OfType<JsonObject>())
                {
                    var child=new CreationParticleEmitter {Name=name+" "+(Children.Count+1),Trigger=trigger,Properties=(JsonObject)node.DeepClone(),
                        Source=Source,Family=Family,Variant=Variant,Texture=Texture,SoftEdge=SoftEdge,Additive=Additive};
                    child.Upgrade();child.Modules!.InheritVelocity=ParentVelocityWeight;Children.Add(child);
                }
            CreationParticleFields.Set(Properties,name,new JsonArray());
        }
        Modules ??= new();
        if(Recipe==CreationParticleRecipe.ReferenceFire){Modules.Shape=ParticleSpawnShape.Box;Modules.Extent=CreationVector.One;}
    }
    public JsonObject NativeProperties()
    {
        var result=(JsonObject)Properties.DeepClone();
        foreach(var (name,trigger) in new[]{("SecondaryParticles",ParticleTrigger.Periodic),("DeathParticles",ParticleTrigger.Death)})
            if(Children.Any(c=>c.Trigger==trigger))CreationParticleFields.Set(result,name,new JsonArray(Children.Where(c=>c.Enabled&&c.Trigger==trigger).Select(c=>(JsonNode)c.NativeProperties()).ToArray()));
        return result;
    }
    public CreationParticleEmitter Duplicate()
    {var copy=CreationJson.Clone(this);var ids=copy.Descendants().ToDictionary(n=>n.Id,_=>Guid.NewGuid());foreach(var n in copy.Descendants()){n.Id=ids[n.Id];if(n.Modules is {} m&&ids.TryGetValue(m.TrailEmitter,out var source))m.TrailEmitter=source;}return copy;}
    public void Validate()
    {
        if (!Enum.IsDefined(Recipe)||ProviderCount is <0 or >256||ProviderIndex<0||(Recipe!=CreationParticleRecipe.None&&(ProviderCount==0||ProviderIndex>=ProviderCount))||Id == Guid.Empty || Name is null || Preparation is null || Source is null || Family is null || Variant is null || Texture is null
            || !Offset.Finite() || !double.IsFinite(Start) || Start is < 0 or > 86400
            || !double.IsFinite(Duration) || Duration is < 0 or > 86400
            || !double.IsFinite(Interval) || Interval is < .001 or > 86400
            || !double.IsFinite(ParentVelocityWeight) || Math.Abs(ParentVelocityWeight) > 1000)
            throw new InvalidDataException("Invalid particle emitter.");
        CreationParticleFields.Validate(Properties);
        if(Children is null || Children.Count>256 || !Enum.IsDefined(Trigger))throw new InvalidDataException("Invalid child nodes.");
        Modules?.Validate();
        if (Legacy is { } legacy) { if (legacy.ParticleEffect is not null) throw new InvalidDataException("Recursive legacy effect."); legacy.ParticleSettings?.Validate(); }
    }
}

public sealed record CreationParticleField(string Name, string Group, string Kind, double Default = 0, int Length = 0);

/// <summary>Vintage Story 1.22.3 AdvancedParticleProperties JSON contract. Unknown keys are retained.</summary>
public static class CreationParticleFields
{
    public static readonly string[] Distributions = ["uniform", "gaussian", "narrowgaussian", "verynarrowgaussian", "invexp", "stronginvexp", "strongerinvexp", "inversegaussian", "narrowinversegaussian", "dirac", "triangle"];
    public static readonly string[] NativeDistributionOrder = ["uniform", "triangle", "gaussian", "narrowgaussian", "inversegaussian", "narrowinversegaussian", "invexp", "stronginvexp", "strongerinvexp", "dirac", "verynarrowgaussian"];
    public static string FunctionName(JsonNode? node, bool distribution)
    {
        string value=node?.ToString() ?? (distribution?"uniform":"IDENTICAL");
        if(int.TryParse(value,out int index))
        {
            string[] names=distribution?NativeDistributionOrder:["IDENTICAL","LINEAR","LINEARNULLIFY","LINEARREDUCE","LINEARINCREASE","QUADRATIC","INVERSELINEAR","ROOT","SINUS","CLAMPEDPOSITIVESINUS","COSINUS","SMOOTHSTEP"];
            if(!distribution && index==12)return "UNSPECIFIED";
            if(index>=0&&index<names.Length)return names[index];
        }
        return value;
    }
    public static readonly string[] Transforms = ["IDENTICAL", "LINEAR", "INVERSELINEAR", "LINEARNULLIFY", "LINEARREDUCE", "LINEARINCREASE", "QUADRATIC", "ROOT", "SINUS", "CLAMPEDPOSITIVESINUS", "COSINUS", "SMOOTHSTEP"];
    public static readonly CreationParticleField[] All = [
        new("Quantity","emission","nat",1), new("LifeLength","emission","nat",1), new("PosOffset","emission","nat",0,3),
        new("Velocity","motion","nat",0,3), new("GravityEffect","motion","nat",1), new("VelocityEvolve","motion","evolve",0,3),
        new("RandomVelocityChange","motion","bool"), new("SelfPropelled","motion","bool"),
        new("Size","appearance","nat",1), new("SizeEvolve","appearance","evolve"),
        new("HsvaColor","appearance","nat",255,4), new("ColorByBlock","appearance","bool"),
        new("OpacityEvolve","appearance","evolve"), new("RedEvolve","appearance","evolve"), new("GreenEvolve","appearance","evolve"), new("BlueEvolve","appearance","evolve"),
        new("ParticleModel","appearance","model"), new("VertexFlags","appearance","int"),
        new("WindAffectednes","environment","number"), new("Bounciness","environment","number"),
        new("TerrainCollision","environment","bool",1), new("DieInAir","environment","bool"), new("DieInLiquid","environment","bool"),
        new("SwimOnLiquid","environment","bool"), new("DieOnRainHeightmap","environment","bool"),
        new("SecondarySpawnInterval","children","nat"), new("SecondaryParticles","children","children"), new("DeathParticles","children","children")];
    public static JsonObject Nat(double avg, double variation = 0) => new() { ["avg"] = avg, ["var"] = variation, ["dist"] = "uniform" };
    public static JsonObject Evolution(string transform = "IDENTICAL", double factor = 0) => new() { ["transform"] = transform, ["factor"] = factor };
    public static JsonNode? Find(JsonObject obj, string key) => obj.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase)).Value;
    public static void Set(JsonObject obj, string key, JsonNode? value)
    { string actual = obj.Select(p => p.Key).FirstOrDefault(k => k.Equals(key, StringComparison.OrdinalIgnoreCase)) ?? key; obj[actual] = value; }
    public static double Number(JsonObject obj, string key, double fallback = 0)
        => double.TryParse(Find(obj,key)?.ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double n) ? n : fallback;
    public static bool Flag(JsonObject obj, string key, bool fallback = false) => bool.TryParse(Find(obj,key)?.ToString(), out bool b) ? b : fallback;
    public static JsonObject Defaults()
    {
        var p = new JsonObject();
        foreach (var f in All)
        {
            JsonNode? Value(int i) => f.Kind switch { "nat" => Nat(f.Name == "HsvaColor" && i == 1 ? 0 : f.Default),
                "evolve" => null, "bool" => JsonValue.Create(f.Default != 0), "model" => JsonValue.Create("Quad"),
                "children" => new JsonArray(), _ => JsonValue.Create(f.Default) };
            p[f.Name] = f.Length > 0 ? new JsonArray(Enumerable.Range(0,f.Length).Select(Value).ToArray()) : Value(0);
        }
        return p;
    }
    public static IEnumerable<string> Warnings(JsonObject p, bool includeRendererWarnings = true)
    {
        foreach (var pair in p)
            if (!All.Any(f => f.Name.Equals(pair.Key,StringComparison.OrdinalIgnoreCase))) yield return "Unknown field retained: " + pair.Key;
        if (includeRendererWarnings && (int)Number(p,"VertexFlags") is var flags && (flags & ~255)!=0) yield return "Director renders VertexFlags glow (low 8 bits); native shader flags are retained for export.";
        if (p.Any(k=>k.Key.Equals("HsvaColor",StringComparison.OrdinalIgnoreCase)) && Find(p,"HsvaColor") is null) yield return "Packed Color is not a native JSON field; supply HsvaColor for portable color.";
        foreach(var field in All.Where(f=>f.Kind=="nat"&&f.Name!="HsvaColor"))
            if(p.Any(k=>k.Key.Equals(field.Name,StringComparison.OrdinalIgnoreCase))&&Find(p,field.Name) is null)
                yield return field.Name+" is null: Director uses its fallback; native apply is unavailable until a value is supplied.";
        foreach (string name in new[]{"SecondaryParticles","DeathParticles"})
            if (Find(p,name) is JsonArray a) foreach (var child in a.OfType<JsonObject>()) foreach (string w in Warnings(child,includeRendererWarnings)) yield return name+": "+w;
    }
    public static void Validate(JsonObject? p, int depth = 0)
    {
        if (p is null || depth > 8) throw new InvalidDataException("Particle recursion exceeds eight levels.");
        foreach (var f in All)
        {
            var node = Find(p,f.Name); if (node is null) continue;
            void Value(JsonNode? n)
            {
                if (n is null) { if(f.Kind=="nat")throw new InvalidDataException("Null "+f.Name+" component."); return; }
                if (f.Kind is "nat" or "evolve")
                {
                    if (n is not JsonObject v) throw new InvalidDataException(f.Name+" must be an object.");
                    foreach (string key in f.Kind == "nat" ? new[]{"avg","var","offset"} : new[]{"factor","maxvalue"})
                        if (Find(v,key) is { } number && (!double.TryParse(number.ToString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double d) || !double.IsFinite(d) || Math.Abs(d)>1e9))
                            throw new InvalidDataException("Invalid "+f.Name+"." );
                    string kind = FunctionName(Find(v,f.Kind=="nat"?"dist":"transform"),f.Kind=="nat");
                    if (!(f.Kind=="nat"?Distributions:Transforms).Contains(kind,StringComparer.OrdinalIgnoreCase) && kind != "UNSPECIFIED") throw new InvalidDataException("Unknown particle function: "+kind);
                }
                else if (f.Kind=="bool" && !bool.TryParse(n.ToString(),out _)) throw new InvalidDataException("Invalid "+f.Name);
                else if (f.Kind is "number" or "int" && (!double.TryParse(n.ToString(),System.Globalization.NumberStyles.Float,System.Globalization.CultureInfo.InvariantCulture,out double d)||!double.IsFinite(d)||Math.Abs(d)>int.MaxValue)) throw new InvalidDataException("Invalid "+f.Name);
                else if (f.Kind=="model" && n.ToString() is not ("Quad" or "Cube" or "0" or "1")) throw new InvalidDataException("Invalid particle model.");
            }
            if (f.Kind=="children")
            { if (node is not JsonArray a || a.Count>256) throw new InvalidDataException("Invalid child particles."); foreach(var c in a) Validate(c as JsonObject,depth+1); }
            else if (f.Length>0)
            { if (node is not JsonArray a || a.Count!=f.Length) throw new InvalidDataException("Invalid "+f.Name+" vector."); foreach(var n in a)Value(n); }
            else Value(node);
        }
    }
    public static CreationParticleEffect ConvertLegacy(CreationSceneObject obj)
    {
        var legacy = CreationJson.Clone(obj); legacy.ParticleEffect = null; legacy.ParentId = legacy.ParentPartId = Guid.Empty;
        var s = legacy.ParticleSettings ?? CreationParticleSettings.Preset(obj.ResourceCode);
        var p = Defaults(); p["Quantity"] = Nat(s.Burst?obj.ParticleCount:1); p["LifeLength"]=Nat(obj.ParticleLife/5,s.LifeSpread/5);
        p["GravityEffect"]=Nat(-s.Acceleration.Y/12); p["Size"]=Nat(s.StartSize*4,s.SizeSpread*4);
        p["TerrainCollision"]=false;
        p["PosOffset"]=new JsonArray(Nat(0,s.Spread.X),Nat(0,s.Spread.Y),Nat(0,s.Spread.Z));
        p["Velocity"]=new JsonArray(Nat(s.Velocity.X,s.VelocitySpread.X),Nat(s.Velocity.Y,s.VelocitySpread.Y),Nat(s.Velocity.Z,s.VelocitySpread.Z));
        p["SizeEvolve"]=Evolution("LINEAR",(s.EndSize-s.StartSize)*4); p["OpacityEvolve"]=Evolution("LINEAR",(s.EndOpacity-s.StartOpacity)*255);
        double r=obj.Color.X,g=obj.Color.Y,b=obj.Color.Z,max=Math.Max(r,Math.Max(g,b)),min=Math.Min(r,Math.Min(g,b)),delta=max-min;
        double h=delta==0?0:max==r?((g-b)/delta+6)%6:max==g?(b-r)/delta+2:(r-g)/delta+4;
        p["HsvaColor"]=new JsonArray(Nat(h/6*255),Nat(max==0?0:delta/max*255),Nat(max*255),Nat(s.StartOpacity*255));
        p["RedEvolve"]=Evolution("LINEAR",(s.EndColor.X-r)*255);p["GreenEvolve"]=Evolution("LINEAR",(s.EndColor.Y-g)*255);p["BlueEvolve"]=Evolution("LINEAR",(s.EndColor.Z-b)*255);
        p["ParticleModel"]=s.Shape==CreationParticleShape.Cube?"Cube":"Quad"; p["VertexFlags"]=(int)(obj.Glow*255);
        return new() { Name=obj.Name, Seed=s.Seed, Emitters=[new() { Name=obj.Name, Legacy=legacy, Properties=p,
            Original=(JsonObject)p.DeepClone(), Duration=obj.ParticleDuration, Interval=1d/Math.Max(1,obj.ParticleCount), Burst=s.Burst, Texture=s.Texture,SoftEdge=s.Shape==CreationParticleShape.SoftQuad }] };
    }
    public static CreationParticleEffect Import(string json)
    {
        var root = JsonNode.Parse(json,new JsonNodeOptions { PropertyNameCaseInsensitive=true },new JsonDocumentOptions { AllowTrailingCommas=true, CommentHandling=JsonCommentHandling.Skip }) ?? throw new InvalidDataException("Empty particle JSON.");
        CreationParticleEffect effect;
        if (root is JsonObject obj && Find(obj,"Emitters") is not null) effect=root.Deserialize<CreationParticleEffect>(CreationJson.Options)!;
        else if(root is JsonObject old && Find(old,"Settings") is not null)
        {
            var preset=root.Deserialize<CreationParticlePreset>(CreationJson.Options)!;preset.Validate();
            effect=ConvertLegacy(new(){ParticleSettings=preset.Settings,ParticleCount=preset.Rate,ParticleLife=preset.Life,ParticleDuration=preset.Duration,Color=preset.Color,Glow=preset.Glow});
            if(preset.TextureData.Length>0)effect.Textures[preset.Settings.Texture]=preset.TextureData;
        }
        else
        {
            IEnumerable<JsonNode?> list = root is JsonArray a ? a : new[]{root};
            effect = new() { Emitters=list.Select((n,i)=>new CreationParticleEmitter { Name="Emitter "+(i+1), Properties=n is JsonObject p?(JsonObject)p.DeepClone():throw new InvalidDataException("Expected particle properties."),Original=n is JsonObject original?(JsonObject)original.DeepClone():null }).ToList() };
        }
        effect.Validate(); return effect;
    }
    public static string ExportNative(CreationParticleEffect effect,bool compatibleOnly=false)
    {
        effect.Validate();
        var issues=ParticleCompatibility.Issues(effect);
        if(effect.Version>=3 && !compatibleOnly && issues.Count>0)throw new InvalidOperationException("Native export would omit: "+string.Join("; ",issues));
        if(effect.Emitters.Any(e=>e.Legacy is not null))throw new InvalidOperationException("Converted legacy effects use the Director evaluator. Save a Director preset to preserve their appearance and timing.");
        JsonObject Native(JsonObject properties)
        {
            var result=new JsonObject();
            foreach(var field in All)
            {
                var value=Find(properties,field.Name);
                if(value is null)continue;
                if(field.Kind=="children")value=new JsonArray(((JsonArray)value).OfType<JsonObject>().Select(c=>(JsonNode)Native(c)).ToArray());
                result[field.Name]=value.DeepClone();
            }
            return result;
        }
        var a=new JsonArray(effect.Emitters.Where(e=>e.Enabled && (!compatibleOnly || e.Modules?.Geometry is null or ParticleGeometry.Particle)).Select(e=>(JsonNode)Native(e.NativeProperties())).ToArray());
        return (a.Count==1?a[0]!:a).ToJsonString(CreationJson.Options);
    }
}
