using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Numerics;
using System.Text.Json.Nodes;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VSDirector;
using VSDirector.Data;

internal static class DirectorAdvancedParticleChecks
{
    internal static void Run()
    {
        var assembly=typeof(VSDirectorModSystem).Assembly;
        var adapter=assembly.GetType("VSDirector.Runtime.DirectorParticleNativeAdapter",true)!;
        object Call(string method,params object?[] args)=>AccessTools.Method(adapter,method).Invoke(null,args)!;
        void Check(bool value,string message){if(!value)throw new Exception(message);}
        var native=new AdvancedParticleProperties();
        var json=(JsonObject)Call("Read",native);
        var names=CreationParticleFields.All.Select(f=>f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Check(json.All(pair=>names.Contains(pair.Key)),"Native JSON contract contains an undescribed member: "+string.Join(",",json.Select(k=>k.Key).Where(k=>!names.Contains(k))));
        foreach(var field in CreationParticleFields.All)Check(json.Any(k=>k.Key.Equals(field.Name,StringComparison.OrdinalIgnoreCase)),"Descriptor is absent from native JSON: "+field.Name);
        foreach(string dist in CreationParticleFields.Distributions)
        foreach(string function in CreationParticleFields.Transforms)
        {
            var p=CreationParticleFields.Defaults();
            p["Quantity"]=new JsonObject{["dist"]=dist,["avg"]=3,["var"]=.2,["offset"]=.1};
            foreach(var field in CreationParticleFields.All.Where(f=>f.Kind=="evolve"))
                p[field.Name]=field.Length==0?CreationParticleFields.Evolution(function,.3):new JsonArray(Enumerable.Range(0,3).Select(_=>(JsonNode)CreationParticleFields.Evolution(function,.3)).ToArray());
            p["SecondaryParticles"]=new JsonArray(CreationParticleFields.Defaults());p["DeathParticles"]=new JsonArray(CreationParticleFields.Defaults());
            var decoded=(AdvancedParticleProperties)Call("Write",p);var roundtrip=(JsonObject)Call("Read",decoded);
            CreationParticleFields.Validate(roundtrip);
            Check(decoded.Quantity.avg==3&&Math.Abs(decoded.Quantity.var-.2)<1e-6&&decoded.SecondaryParticles.Length==1&&decoded.DeathParticles.Length==1,"Native adapter lost random or child parameters");
            foreach(var field in CreationParticleFields.All.Where(f=>f.Kind=="evolve"&&f.Length==0))
                Check(CreationParticleFields.FunctionName(CreationParticleFields.Find((JsonObject)CreationParticleFields.Find(roundtrip,field.Name)!,"transform"),false)==function,"Native evolution enum mismatch");
        }
        Call("RequireSafe",CreationParticleFields.Defaults());
        var flagged=CreationParticleFields.Defaults();flagged["VertexFlags"]=512;
        Call("RequireSafe",flagged);
        Check(CreationParticleFields.Warnings(flagged).Any(),"Unsupported authoring shader flags were not explained");
        foreach(string function in CreationParticleFields.Transforms)
        {
            var props=CreationParticleFields.Defaults();props["SizeEvolve"]=CreationParticleFields.Evolution(function,.3);
            var provider=(AdvancedParticleProperties)Call("Write",props);
            Check(Math.Abs(provider.SizeEvolve.nextFloat(10,.5f)-DirectorCreationParticleSimulation.Evolve(props["SizeEvolve"],10,.5))<.001,"Native evolution evaluation differs: "+function);
        }
        var packed=new AdvancedParticleProperties{HsvaColor=null,Color=unchecked((int)0x803366cc)};
        var portable=(AdvancedParticleProperties)Call("Write",Call("Read",packed));
        int portableColor=portable.GetRgbaColor(null!);
        for(int channel=0;channel<4;channel++)Check(Math.Abs(((portableColor>>(channel*8))&255)-((packed.Color>>(channel*8))&255))<=4,$"Packed runtime color lost during portable resource copy: {packed.Color:x8} => {portableColor:x8}, {Call("Read",packed)}");
        foreach(int hue in new[]{0,42,43,85,128,170,213,255})
        {
            var props=CreationParticleFields.Defaults();props["HsvaColor"]=new JsonArray(CreationParticleFields.Nat(hue),CreationParticleFields.Nat(191),CreationParticleFields.Nat(204),CreationParticleFields.Nat(128));
            props["GravityEffect"]=CreationParticleFields.Nat(0);props["RedEvolve"]=CreationParticleFields.Evolution("LINEAR",-10);
            var provider=(AdvancedParticleProperties)Call("Write",props);int rgba=provider.GetRgbaColor(null!);
            var effect=new CreationSceneObject{Kind=CreationObjectKind.Particle,ParticleEffect=new()};effect.ParticleEffect.Emitters[0].Burst=true;effect.ParticleEffect.Emitters[0].Properties=props;
            var simulation=new DirectorCreationParticleSimulation(new(){Objects=[effect]},(_,_)=>Matrix4x4.Identity);
            Check(simulation.Seek(0),"Initial color simulation did not complete");var particle=simulation.Particles.Single();
            double red=Math.Clamp((((rgba>>16)&255)+provider.RedEvolve.nextFloat((rgba>>16)&255,0))/255d,0,1);
            Check(Math.Abs(particle.Color.X-red)<1e-6&&Math.Abs(particle.Color.Y-((rgba>>8)&255)/255d)<1e-6&&Math.Abs(particle.Color.Z-(rgba&255)/255d)<1e-6,"HSVA or native additive RGB evolution differs at hue "+hue);
        }
        var runtimeType=assembly.GetType("VSDirector.Runtime.DirectorCreationRuntime",true)!;
        var runtime=RuntimeHelpers.GetUninitializedObject(runtimeType);var extensions=new DirectorCreationExtensions();extensions.Register(new ParticleBehavior());
        AccessTools.Field(runtimeType,"<Extensions>k__BackingField").SetValue(runtime,extensions);
        var parent=new CreationSceneObject{Transform=new(){Position=new(2,0,0)}};
        var attached=new CreationSceneObject{Kind=CreationObjectKind.Particle,ParentId=parent.Id,Transform=new(){Position=new(3,0,0)}};
        var authored=new DirectorCreationProject{Objects=[parent,attached]};var evaluated=DirectorCreationEvaluator.Evaluate(authored,.5);
        AccessTools.Method(runtimeType,"PrepareRuntimeFrame").Invoke(runtime,[authored,evaluated,false]);
        Check(Math.Abs(evaluated.Objects[attached.Id].World.Translation.X-10)<1e-6,"Emission frame did not compose runtime behaviors and parent transforms");
        var unsafeProperties=CreationParticleFields.Defaults();unsafeProperties["VelocityEvolve"]=new JsonArray(CreationParticleFields.Evolution(),CreationParticleFields.Evolution(),CreationParticleFields.Evolution());
        bool rejected=false;try{Call("RequireSafe",unsafeProperties);}catch(TargetInvocationException e){rejected=e.InnerException!.Message.Contains("VelocityEvolve");}
        Check(rejected,"Unsafe native Clone bug was not rejected");
        native.basePos.Set(12,34,56);native.baseVelocity.Set(1,2,3);native.ParentVelocity=new(4,5,6);native.ParentVelocityWeight=.3f;
        var clone=(AdvancedParticleProperties)Call("Clone",native);
        Check(clone.basePos.X==12&&clone.baseVelocity.Y==2&&clone.ParentVelocity.Z==6&&!ReferenceEquals(clone.Quantity,native.Quantity),"Native clone lost runtime context or shared mutable parameters");
        var item=new Item{Code=new("game:particle-probe"),ParticleProperties=[native]};var original=item.ParticleProperties;
        var resourceType=assembly.GetType("VSDirector.Runtime.DirectorParticleResource",true)!;
        var resource=Activator.CreateInstance(resourceType,["item:game:particle-probe#0","item:game:particle","game:particle-probe","game:particle-probe","item",0,json,item])!;
        var resources=Array.CreateInstance(resourceType,1);resources.SetValue(resource,0);
        var managerType=assembly.GetType("VSDirector.Runtime.DirectorCreationParticleOverrides",true)!;
        using var manager=(IDisposable)Activator.CreateInstance(managerType,true)!;
        var obj=new CreationSceneObject{Kind=CreationObjectKind.Particle,ParticleEffect=new()};var emitter=obj.ParticleEffect.Emitters[0];emitter.Properties["Quantity"]=CreationParticleFields.Nat(9);
        AccessTools.Method(managerType,"Apply").Invoke(manager,[resources,obj,emitter]);
        Check(item.ParticleProperties[0].Quantity.avg==9&&original[0].Quantity.avg==1,"Client apply mutated the original snapshot");
        Check(item.ParticleProperties[0].basePos.X==12&&item.ParticleProperties[0].ParentVelocity!.Z==6,"Applied provider lost runtime context");
        emitter.Properties["Quantity"]=CreationParticleFields.Nat(5);
        AccessTools.Method(managerType,"Refresh").Invoke(manager,[new DirectorCreationProject{Objects=[obj]}]);
        Check(item.ParticleProperties[0].Quantity.avg==5,"Undo/edit refresh did not update applied provider");
        AccessTools.Method(managerType,"Restore").Invoke(manager,[null]);
        Check(ReferenceEquals(item.ParticleProperties,original),"Restore all did not restore exact original providers");
        item.ParticleProperties=[native,(AdvancedParticleProperties)Call("Write",unsafeProperties)];
        rejected=false;try{AccessTools.Method(managerType,"Apply").Invoke(manager,[resources,obj,emitter]);}catch(TargetInvocationException error){rejected=error.InnerException!.Message.Contains("VelocityEvolve");}
        Check(rejected&&item.ParticleProperties[0]==native,"Unsafe native sibling accepted or partially applied");
        object Resources(CollectibleObject collectible)
        {
            var entry=Activator.CreateInstance(resourceType,["block:"+collectible.Code+"#0","probe",collectible.Code.ToString(),collectible.Code.ToString(),"block",0,json,collectible])!;
            var list=Array.CreateInstance(resourceType,1);list.SetValue(entry,0);return list;
        }
        var first=new ProbeBlock{Code=new("game:particle-first"),ParticleProperties=[native]};
        var second=new OtherProbeBlock{Code=new("game:particle-second"),ParticleProperties=[native]};
        var snapshot=first.ParticleProperties;
        foreach(var block in new ProbeBlock[]{first,second})
        {
            AccessTools.Method(managerType,"Apply").Invoke(manager,[Resources(block),obj,emitter]);
            // Native particle ticking often uses a separate Block instance with the same code.
            var runtimeCopy=(ProbeBlock)Activator.CreateInstance(block.GetType(),true)!;runtimeCopy.Code=block.Code;runtimeCopy.ParticleProperties=[native];var runtimeOriginal=runtimeCopy.ParticleProperties;
            runtimeCopy.OnAsyncClientParticleTick(null!,new(0,0,0),0,0);
            Check(runtimeCopy.Seen==5&&ReferenceEquals(runtimeCopy.ParticleProperties,runtimeOriginal),"Derived native block callback was not patched/restored");
        }
        using var entered=new ManualResetEventSlim();using var release=new ManualResetEventSlim();using var restoring=new ManualResetEventSlim();
        first.Entered=entered;first.Release=release;
        var ticking=Task.Run(()=>first.OnAsyncClientParticleTick(null!,new(0,0,0),0,0));
        Check(entered.Wait(5000),"Async particle callback did not start");
        var restore=Task.Run(()=>{restoring.Set();AccessTools.Method(managerType,"Restore").Invoke(manager,[null]);});
        try{Check(restoring.Wait(5000)&&!restore.Wait(30),"Restore raced an active native callback");}
        finally{release.Set();}
        Check(Task.WaitAll([ticking,restore],5000)&&ReferenceEquals(first.ParticleProperties,snapshot),"Callback finalizer overwrote restored native properties");
        var impulse=new SamplingProbeBlock{Code=new("game:particle-impulse"),ParticleProperties=[native]};
        emitter.Properties["Quantity"]=new JsonObject{["avg"]=9,["var"]=0,["dist"]="dirac"};
        AccessTools.Method(managerType,"Apply").Invoke(manager,[Resources(impulse),obj,emitter]);
        impulse.OnAsyncClientParticleTick(null!,new(0,0,0),0,0);Check(impulse.Seen==9,"Native DIRAC impulse did not fire");
        impulse.OnAsyncClientParticleTick(null!,new(0,0,0),0,0);Check(impulse.Seen==0,"Native DIRAC impulse restarted on every callback");
        AccessTools.Method(managerType,"Restore").Invoke(manager,[null]);Check(impulse.ParticleProperties[0]==native&&native.Quantity.avg==1,"Native impulse mutated the original provider");
        Console.WriteLine("PASS native block clone callbacks, multiple derived entry points, unsafe siblings and synchronized restoration");
        Console.WriteLine("PASS native 1.22.3 particle field contract, distributions/evolutions, clone safety, client apply/edit/restore");
    }
    private class ProbeBlock:Block
    {
        internal float Seen;
        internal ManualResetEventSlim? Entered,Release;
        public override void OnAsyncClientParticleTick(IAsyncParticleManager manager,BlockPos pos,float windAffectednessAtPos,float secondsTicking)
        {Entered?.Set();if(Release is not null&&!Release.Wait(5000))throw new Exception("Particle callback release timed out");Seen=ParticleProperties[0].Quantity.avg;}
    }
    private sealed class ParticleBehavior:ICreationAnimationBehavior
    {
        public string Id=>"particle-probe";
        public void Evaluate(CreationObjectState state,double time)
        {if(state.Source.Kind!=CreationObjectKind.Particle){state.Transform=state.Transform with{Position=state.Transform.Position+new CreationVector(time*10,0,0)};state.World=state.Transform.Matrix();}}
    }
    private sealed class OtherProbeBlock:ProbeBlock
    {
        public override void OnAsyncClientParticleTick(IAsyncParticleManager manager,BlockPos pos,float windAffectednessAtPos,float secondsTicking)
        {Seen=ParticleProperties[0].Quantity.avg;}
    }
    private sealed class SamplingProbeBlock:ProbeBlock
    {
        public override void OnAsyncClientParticleTick(IAsyncParticleManager manager,BlockPos pos,float windAffectednessAtPos,float secondsTicking)
        {Seen=ParticleProperties[0].Quantity.nextFloat();}
    }
}
