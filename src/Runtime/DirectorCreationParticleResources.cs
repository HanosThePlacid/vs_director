using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using VSDirector.Data;

namespace VSDirector.Runtime;

internal static class DirectorParticleNativeAdapter
{
    internal static JsonObject Read(AdvancedParticleProperties p)
    {
        var json=(JsonObject)JsonNode.Parse(JsonUtil.ToString(p))!;
        // Packed runtime Color is not serialized by the engine. Make resource copies portable.
        void Colors(AdvancedParticleProperties source,JsonObject target)
        {
            if(source.HsvaColor is null)
            {
                // GetRgbaColor returns ARGB after swapping HsvToRgba's red/blue bytes.
                var hsv=ColorUtil.RgbToHsvInts((source.Color>>16)&255,(source.Color>>8)&255,source.Color&255);
                // The native inverse uses 255-based hue, while the forward converter
                // uses six 43-wide integer sectors. Select the nearest native hue.
                int bestHue=hsv[0],bestError=int.MaxValue;
                for(int hue=0;hue<256;hue++)
                {
                    int rgb=ColorUtil.HsvToRgba(hue,hsv[1],hsv[2],255),error=0;
                    for(int channel=0;channel<3;channel++)
                    {int delta=((rgb>>(channel*8))&255)-((source.Color>>((2-channel)*8))&255);error+=delta*delta;}
                    if(error<bestError){bestError=error;bestHue=hue;}
                }
                hsv[0]=bestHue;
                CreationParticleFields.Set(target,"HsvaColor",new JsonArray(CreationParticleFields.Nat(hsv[0]),CreationParticleFields.Nat(hsv[1]),CreationParticleFields.Nat(hsv[2]),CreationParticleFields.Nat((uint)source.Color>>24)));
            }
            foreach(var (key,children) in new[]{("SecondaryParticles",source.SecondaryParticles),("DeathParticles",source.DeathParticles)})
                if(children is not null && CreationParticleFields.Find(target,key) is JsonArray nodes)
                    for(int i=0;i<children.Length;i++)if(nodes[i] is JsonObject child)Colors(children[i],child);
        }
        Colors(p,json);return json;
    }
    internal static AdvancedParticleProperties Write(JsonObject p)
    {
        CreationParticleFields.Validate(p);
        var native=(JsonObject)p.DeepClone();
        void Normalize(JsonObject node)
        {
            foreach(var field in CreationParticleFields.All)
            {
                var value=CreationParticleFields.Find(node,field.Name);
                if(field.Kind=="evolve" && (value is null || value is JsonArray a&&a.All(n=>n is null)))
                {var key=node.Select(p=>p.Key).FirstOrDefault(k=>k.Equals(field.Name,StringComparison.OrdinalIgnoreCase));if(key is not null)node.Remove(key);}
                if(field.Kind=="children"&&value is JsonArray children)foreach(var child in children.OfType<JsonObject>())Normalize(child);
            }
        }
        Normalize(native);
        return JsonUtil.FromString<AdvancedParticleProperties>(native.ToJsonString()) ?? throw new InvalidDataException("Invalid native particle properties.");
    }
    internal static AdvancedParticleProperties Clone(AdvancedParticleProperties p)
    {
        var copy=Write(Read(p));
        Context(p,copy);return copy;
    }
    internal static void Context(AdvancedParticleProperties p,AdvancedParticleProperties copy)
    {
        copy.basePos=p.basePos.Clone();copy.baseVelocity=p.baseVelocity.Clone();copy.block=p.block;copy.Color=p.Color;
        copy.ParentVelocity=p.ParentVelocity?.Clone();copy.ParentVelocityWeight=p.ParentVelocityWeight;
        copy.WindAffectednesAtPos=p.WindAffectednesAtPos;copy.IgnoreUserConfig=p.IgnoreUserConfig;
    }
    internal static void RequireSafe(JsonObject p)
    {
        CreationParticleFields.Validate(p);
        var warnings=CreationParticleFields.Warnings(p,false).ToArray();
        if(warnings.Length>0)throw new InvalidOperationException("Native apply rejected: "+string.Join("; ",warnings));
        void Check(JsonObject node)
        {
            if(CreationParticleFields.Find(node,"VelocityEvolve") is JsonArray a && a.Any(n=>n is not null))
                throw new InvalidOperationException("Vintage Story 1.22.3 AdvancedParticleProperties.Clone cannot copy VelocityEvolve safely. Save and simulate this field in Director; native apply is unavailable.");
            foreach(string key in new[]{"SecondaryParticles","DeathParticles"})if(CreationParticleFields.Find(node,key) is JsonArray children)foreach(var child in children.OfType<JsonObject>())Check(child);
        }
        Check(p);
        var provider=Write(p);
        // The engine also serializes HsvaColor without a null guard in Clone().
        void Required(AdvancedParticleProperties value)
        {
            if(value.HsvaColor is null || value.PosOffset is null || value.Velocity is null || value.Quantity is null || value.Size is null || value.LifeLength is null || value.GravityEffect is null || value.SecondarySpawnInterval is null)
                throw new InvalidOperationException("Native apply rejected: a required native particle value is null.");
            if(value.SecondaryParticles?.Length>4)throw new InvalidOperationException("Vintage Story 1.22.3 has only four secondary spawn timers; native apply supports at most four secondary emitters per particle.");
            foreach(var child in (value.SecondaryParticles??[]).Concat(value.DeathParticles??[]))Required(child);
        }
        Required(provider);
    }
}

internal sealed record DirectorParticleResource(string Id,string Family,string Variant,string Source,string Kind,int Index,JsonObject Properties,CollectibleObject? Collectible=null)
{
    internal CreationParticleEmitter Copy() => new(){Name=Variant+" / "+Index,Source=Id,Family=Family,Variant=Variant,
        Properties=(JsonObject)Properties.DeepClone(),Original=(JsonObject)Properties.DeepClone()};
}

/// <summary>Incremental main-thread asset scan; search uses only the completed in-memory catalogue.</summary>
internal sealed class DirectorCreationParticleResources(ICoreClientAPI api) : IDisposable
{
    internal List<DirectorParticleResource> Entries { get; } = new();
    internal List<string> Diagnostics { get; } = new();
    private IEnumerator<Action>? scan;
    private bool scanned;
    internal bool Scanning => scan is not null;
    internal int Scanned { get; private set; }
    internal void Start(bool force=false)
    {
        if(Scanning || scanned&&!force)return;
        Entries.Clear();Diagnostics.Clear();Scanned=0;scan=Scan().GetEnumerator();scanned=true;
    }
    internal void Tick()
    {
        if(scan is null)return;var timer=Stopwatch.StartNew();
        do
        {
            if(!scan.MoveNext()){scan.Dispose();scan=null;Entries.Sort((a,b)=>StringComparer.OrdinalIgnoreCase.Compare(a.Id,b.Id));break;}
            try{scan.Current();}catch(Exception e){Diagnostics.Add(e.Message);}Scanned++;
        }while(timer.ElapsedMilliseconds<3);
    }
    private IEnumerable<Action> Scan()
    {
        foreach(var collectible in api.World.Blocks.Cast<CollectibleObject>().Concat(api.World.Items))
            if(collectible?.Code is not null && collectible.ParticleProperties is {Length:>0})yield return ()=>
            {
                string kind=collectible is Block?"block":"item",code=collectible.Code.ToString();
                string family=kind+":"+(collectible.VariantStrict.Count==0?code:collectible.CodeWithVariants(collectible.VariantStrict.ToDictionary(v=>v.Key,_=>"*")));
                for(int i=0;i<collectible.ParticleProperties.Length;i++)
                {
                    if(collectible.ParticleProperties[i] is not { } p)continue;
                    Entries.Add(new($"{kind}:{code}#ParticleProperties[{i}]",family,code,code,kind,i,DirectorParticleNativeAdapter.Read(p),collectible));
                }
            };
        foreach(var asset in api.Assets.AllAssets.Values.ToArray())
        {
            string path=asset.Location.Path;
            if(!path.EndsWith(".json",StringComparison.OrdinalIgnoreCase))continue;
            if(!(path.StartsWith("blocktypes/")||path.StartsWith("itemtypes/")||path.StartsWith("entities/")||path.Contains("particle",StringComparison.OrdinalIgnoreCase)))continue;
            yield return ()=>
            {
                bool loaded=asset.IsLoaded();
                try
                {
                    if(!loaded&&!asset.Origin.TryLoadAsset(asset))return;
                    string text=asset.ToText();
                    bool named=path.StartsWith("config/")&&path.Contains("particle",StringComparison.OrdinalIgnoreCase);
                    if(!named&&!text.Contains("particle",StringComparison.OrdinalIgnoreCase))return;
                    var root=JsonNode.Parse(JsonUtil.ToString(JsonUtil.FromString<object>(text)));
                    if(root is null)return;
                    void Add(JsonNode node,string pointer,string family,string variant)
                    {
                        var list=node is JsonArray a?a.ToArray():new[]{node};int i=0;
                        foreach(var p in list.OfType<JsonObject>())
                        {
                            if(!p.Any(k=>CreationParticleFields.All.Any(f=>f.Name.Equals(k.Key,StringComparison.OrdinalIgnoreCase))))continue;
                            Entries.Add(new(asset.Location+"#"+pointer+"["+i+"]",family,variant,asset.Location.ToString(),named?"named":"embedded",i++,(JsonObject)p.DeepClone()));
                        }
                    }
                    void Walk(JsonNode node,string pointer)
                    {
                        if(node is JsonObject o)foreach(var pair in o)
                        {
                            if(pair.Value is null)continue;string child=pointer+"/"+pair.Key;
                            if(pair.Key.Contains("particleProperties",StringComparison.OrdinalIgnoreCase))Add(pair.Value,child,asset.Location.ToString(),pointer.Length==0?asset.Location.ToString():pointer);
                            else Walk(pair.Value,child);
                        }
                        else if(node is JsonArray a)for(int i=0;i<a.Count;i++)if(a[i] is { } c)Walk(c,pointer+"/"+i);
                    }
                    if(named&&root is JsonObject config)
                    {foreach(var pair in config)if(pair.Value is { } value)Add(value,pair.Key,asset.Location.ToString(),asset.Location.Domain+":"+pair.Key);}
                    if(!named)Walk(root,"");
                }
                finally{if(!loaded&&!asset.IsPatched)asset.Data=null!;}
            };
        }
    }
    public void Dispose(){scan?.Dispose();scan=null;Entries.Clear();}
}

/// <summary>Client-only collectible overrides. No disk writes or network messages.</summary>
internal sealed class DirectorCreationParticleOverrides : IDisposable
{
    private const string HarmonyId="vsdirector.creation-particle-overrides";
    private static DirectorCreationParticleOverrides? active;
    private readonly Dictionary<CollectibleObject,AdvancedParticleProperties[]> originals=new();
    private readonly Dictionary<(CollectibleObject,int),(Guid Object,Guid Emitter,JsonObject Properties)> bindings=new();
    // The callback temporarily swaps ParticleProperties. Updates and restoration must
    // hold this same gate until the callback finalizer restores its snapshot.
    private readonly object gate=new();
    private readonly Dictionary<string,AdvancedParticleProperties[]> blockCopies=new(StringComparer.Ordinal);
    private readonly ConditionalWeakTable<Block,AdvancedParticleProperties[]> runtimeCopies=new();
    private readonly HashSet<MethodBase> patched=new();
    internal int Count=>bindings.Count;
    internal string Notice {get;private set;}="";
    internal void Apply(IEnumerable<DirectorParticleResource> resources,CreationSceneObject obj,CreationParticleEmitter emitter)
    {
        if(obj.ParticleEffect?.Version>=3 && ParticleCompatibility.Issues(new CreationParticleEffect {Version=3,Emitters=[emitter]}).Count>0)throw new InvalidOperationException("This node uses Director-only features; native apply would omit them.");
        DirectorParticleNativeAdapter.RequireSafe(emitter.NativeProperties());
        var targets=resources.ToArray();
        if(targets.Length==0||targets.Any(r=>r.Collectible is null))throw new InvalidOperationException("This asset has no supported collectible particle entry point.");
        foreach(var c in targets.Select(r=>r.Collectible!).Distinct())
        {
            var providers=originals.GetValueOrDefault(c,c.ParticleProperties);
            foreach(var r in targets.Where(r=>ReferenceEquals(r.Collectible,c)))
                if(r.Index<0||r.Index>=providers.Length)throw new InvalidOperationException("Particle resource changed; rescan before applying.");
            for(int i=0;i<providers.Length;i++)
                if(!targets.Any(r=>ReferenceEquals(r.Collectible,c)&&r.Index==i))DirectorParticleNativeAdapter.RequireSafe(DirectorParticleNativeAdapter.Read(providers[i]));
        }
        lock(gate)foreach(var r in targets)
        {
            var c=r.Collectible!;
            originals.TryAdd(c,c.ParticleProperties);
            bindings[(c,r.Index)]=(obj.Id,emitter.Id,emitter.NativeProperties());
        }
        Refresh(null);active=this;
        {
            var harmony=new Harmony(HarmonyId);
            foreach(var method in originals.Keys.OfType<Block>().Select(b=>AccessTools.Method(b.GetType(),nameof(Block.OnAsyncClientParticleTick))).Where(m=>m is not null).Distinct())
                if(patched.Add(method!))harmony.Patch(method,prefix:new HarmonyMethod(typeof(DirectorCreationParticleOverrides),nameof(Prefix)),finalizer:new HarmonyMethod(typeof(DirectorCreationParticleOverrides),nameof(Finalizer)));
        }
    }
    internal void Refresh(DirectorCreationProject? project)
    {
        Notice="";
        foreach(var key in bindings.Keys.ToArray())
        {
            var b=bindings[key];
            if(project is null)continue;
            var e=project.Objects.FirstOrDefault(o=>o.Id==b.Object)?.ParticleEffect?.AllNodes().FirstOrDefault(e=>e.Id==b.Emitter);
            if(e is null){bindings.Remove(key);continue;}
            try
            {
                if(e.Modules is not null && ParticleCompatibility.Issues(new(){Version=3,Emitters=[e]}).Count>0)
                    throw new InvalidOperationException(e.Name+": native replacement restored because Director-only features were added.");
                DirectorParticleNativeAdapter.RequireSafe(e.NativeProperties());bindings[key]=(b.Object,b.Emitter,e.NativeProperties());
            }
            catch(Exception error){bindings.Remove(key);Notice=error.Message;}
        }
        lock(gate)
        {
            blockCopies.Clear();
            runtimeCopies.Clear();
            foreach(var (c,original) in originals.ToArray())
            {
                var edits=bindings.Where(b=>ReferenceEquals(b.Key.Item1,c)).ToArray();
                if(edits.Length==0){c.ParticleProperties=original;originals.Remove(c);continue;}
                var copy=original.Select(DirectorParticleNativeAdapter.Clone).ToArray();
                foreach(var edit in edits)
                {
                    var provider=DirectorParticleNativeAdapter.Write(edit.Value.Properties);
                    DirectorParticleNativeAdapter.Context(original[edit.Key.Item2],provider);copy[edit.Key.Item2]=provider;
                }
                c.ParticleProperties=copy;
                if(c is Block)blockCopies[c.Code.ToString()]=copy;
            }
        }
    }
    internal void Restore(IEnumerable<DirectorParticleResource>? resources=null)
    {
        if(resources is null)bindings.Clear();
        else foreach(var r in resources)if(r.Collectible is { } c)bindings.Remove((c,r.Index));
        Refresh(null);
        if(bindings.Count==0){new Harmony(HarmonyId).UnpatchAll(HarmonyId);patched.Clear();active=null;}
    }
    private sealed record State(AdvancedParticleProperties[] Original,object Sync);
    private static void Prefix(Block __instance,out State? __state)
    {
        __state=null;var manager=active;if(manager is null)return;
        Monitor.Enter(manager.gate);
        if(!manager.blockCopies.TryGetValue(__instance.Code.ToString(),out var copy)){Monitor.Exit(manager.gate);return;}
        try
        {
            __state=new(__instance.ParticleProperties,manager.gate);
            // Each runtime Block keeps independent mutable distributions between
            // callbacks. Re-cloning every tick would repeat DIRAC impulses forever.
            __instance.ParticleProperties=manager.runtimeCopies.GetValue(__instance,_=>copy.Select(DirectorParticleNativeAdapter.Clone).ToArray());
        }
        catch{Monitor.Exit(manager.gate);__state=null;throw;}
    }
    private static Exception? Finalizer(Block __instance,State? __state,Exception? __exception)
    {if(__state is not null){try{__instance.ParticleProperties=__state.Original;}finally{Monitor.Exit(__state.Sync);}}return __exception;}
    public void Dispose()=>Restore();
}
