using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VSDirector.Data;

internal static class CreationParticleModuleChecks
{
    private static void Check(bool ok,string error){if(!ok)throw new Exception(error);}
    internal static void Run()
    {
        var legacy=new CreationParticleEffect();var root=legacy.Emitters[0];
        root.Properties["DeathParticles"]=new JsonArray(CreationParticleFields.Defaults());
        string original=JsonSerializer.Serialize(legacy,CreationJson.Options);
        var round=CreationParticleFields.Import(original);Check(round.Version==2&&round.Emitters[0].Children.Count==0,"Loading migrated v2");
        round.Upgrade();round.Validate();Check(round.Version==3&&round.AllNodes().Count()==2,"Upgrade lost children");
        Check(JsonNode.DeepEquals(round.Emitters[0].NativeProperties(),root.Properties),"Upgrade changed native parameters");
        var duplicate=round.Emitters[0].Duplicate();Check(!duplicate.Descendants().Select(n=>n.Id).Intersect(round.AllNodes().Select(n=>n.Id)).Any(),"Duplicated nested ID");
        var context=new ParticleEvaluation(2,1,.5,4,new(3,4,0),123,new(1,0,0));
        Check(ParticleExpression.Compile("speed == 5 ? sin(pi / 2) + age : 0").Evaluate(context)==1.5,"Expression evaluation wrong");
        Check(ParticleExpression.Compile("1 ? 2 : 1/0").Evaluate(context)==2,"Conditional did not short circuit");
        Check(ParticleExpression.Compile("random(4)").Evaluate(context)==ParticleExpression.Compile("random(4)").Evaluate(context),"Random is nondeterministic");
        foreach(string invalid in new[]{"System.IO.File.ReadAllText(1)","while(1)",new string('(',80)+"1"+new string(')',80)})
        {bool rejected=false;try{ParticleExpression.Compile(invalid);}catch(InvalidDataException){rejected=true;}Check(rejected,"Unsafe expression accepted");}
        var curve=new ParticleCurve{Points=[new(){Time=0,Value=0},new(){Time=1,Value=2}]};Check(curve.Sample(.5)==1,"Linear curve wrong");curve.Mode=ParticleCurveMode.Step;Check(curve.Sample(.5)==0,"Step curve wrong");
        var effect=CreationParticleEffect.Create();var emitter=effect.Emitters[0];emitter.Burst=true;
        emitter.Properties["TerrainCollision"]=false;emitter.Properties["GravityEffect"]=CreationParticleFields.Nat(0);emitter.Properties["LifeLength"]=CreationParticleFields.Nat(1);
        emitter.Modules!.Shape=ParticleSpawnShape.Sphere;emitter.Modules.Surface=true;emitter.Modules.Extent=new(2,2,2);
        emitter.Modules.Drivers["Size"]=new(){Mode=ParticleValueMode.Expression,Expression="2 + age",Clock=ParticleClock.Life};
        var obj=new CreationSceneObject{Kind=CreationObjectKind.Particle,ParticleEffect=effect,ParticleDuration=5};var project=new DirectorCreationProject{Objects=[obj]};
        DirectorCreationParticleSimulation New()=>new(project,(_,t)=>Matrix4x4.CreateTranslation((float)t,0,0));
        void Seek(DirectorCreationParticleSimulation s,double t){int iterations=0;while(!s.Seek(t,37))if(++iterations>10000)throw new Exception("Seek stalled: "+s.Error);}
        var sim=New();Seek(sim,0);Check(Math.Abs(sim.Particles.Single().Position.Float().Length()-2)<1e-5,"Sphere surface sampling wrong");
        Seek(sim,1);var expected=sim.Particles.ToArray();Check(Math.Abs(expected[0].Size-.55)<1e-6,"Size expression not applied");
        Seek(sim,2);Seek(sim,1);Check(expected.SequenceEqual(sim.Particles),"Module rewind mismatch");
        foreach(int fps in new[]{24,30,60,120}){var export=New();for(int i=0;i<=fps;i++)Seek(export,(double)i/fps);Check(expected.SequenceEqual(export.Particles),"Module FPS mismatch");}
        var child=new CreationParticleEmitter{Modules=new(),Trigger=ParticleTrigger.Birth};child.Properties["TerrainCollision"]=false;emitter.Children.Add(child);sim=New();Seek(sim,0);Check(sim.Particles.Count==2,"Birth event missing");
        emitter.Children.Clear();emitter.Modules.Geometry=ParticleGeometry.Beam;sim=New();Seek(sim,1);Check(sim.Particles.Count==0&&sim.Ribbons.Count==1,"Beam did not replace root particles");
        emitter.Modules.Geometry=ParticleGeometry.Lightning;sim=New();Seek(sim,1);var geometry=JsonSerializer.Serialize(sim.Ribbons);Seek(sim,2);Seek(sim,1);Check(geometry==JsonSerializer.Serialize(sim.Ribbons),"Lightning rewind mismatch");
        emitter.Modules.Geometry=ParticleGeometry.Trail;sim=New();Seek(sim,1);Check(sim.Ribbons.Count==1&&sim.Ribbons[0].Points.Length>2,"Trail missing");
        Check(ParticleCompatibility.Issues(effect).Count>0,"Extensions reported native compatible");bool refused=false;try{CreationParticleFields.ExportNative(effect);}catch(InvalidOperationException){refused=true;}Check(refused,"Native export silently discarded modules");
        var copy=CreationParticleFields.Import(JsonSerializer.Serialize(effect,CreationJson.Options));Check(copy.Emitters[0].Modules!.Geometry==ParticleGeometry.Trail,"Preset lost geometry");
        // Upgrading must retain exact emission times, native random samples and world transforms.
        effect=new(){Emitters=[new(){Interval=.013,Duration=.2}]};emitter=effect.Emitters[0];
        emitter.Properties["TerrainCollision"]=false;emitter.Properties["GravityEffect"]=CreationParticleFields.Nat(0);
        emitter.Properties["Size"]=CreationParticleFields.Nat(1,.2);emitter.Properties["Quantity"]=CreationParticleFields.Nat(1.5,.2);
        obj.ParticleEffect=effect;sim=New();Seek(sim,.3);var before=sim.Particles.ToArray();effect.Upgrade();sim=New();Seek(sim,.3);
        Check(before.Length==sim.Particles.Count && before.Zip(sim.Particles).All(pair=>pair.First.Index==pair.Second.Index && Vector3.Distance(pair.First.Position.Float(),pair.Second.Position.Float())<1e-7 && Math.Abs(pair.First.Size-pair.Second.Size)<1e-7 && pair.First.Color==pair.Second.Color),"Upgrade changed emission timing, seeds or output");
        emitter.Burst=true;emitter.Modules!.Drivers["Size"]=new(){Mode=ParticleValueMode.Expression,Expression="1+emitterTime",Clock=ParticleClock.Emission};
        sim=New();Seek(sim,.1);Check(sim.Particles.All(p=>Math.Abs(p.Size-.25)<1e-8),"Emission driver was not frozen at birth");
        emitter.Modules.Drivers["Velocity.X"]=new(){Mode=ParticleValueMode.Expression,Expression="time",Clock=ParticleClock.Project};
        sim=New();Seek(sim,.1);Check(sim.Particles.All(p=>Math.Abs(p.Velocity.X-.1)<1e-8),"Project velocity driver was ignored");
        emitter.Burst=false;emitter.Duration=1;emitter.Properties["Quantity"]=CreationParticleFields.Nat(1);emitter.Modules.Drivers.Clear();
        emitter.Modules.Drivers["Interval"]=new(){Mode=ParticleValueMode.Expression,Expression="0.01+emitterTime*0.1"};
        sim=New();Seek(sim,.5);int count=0;for(double t=0;t<=.5+1e-8;t+=.01+t*.1)count++;
        Check(sim.Particles.Count==count,"Variable interval scheduling skipped or duplicated emission");
        var scheduled=sim.Particles.ToArray();Seek(sim,.1);Seek(sim,.5);Check(scheduled.SequenceEqual(sim.Particles),"Schedule accumulator rewind mismatch");
        emitter.Modules.Drivers.Clear();emitter.Modules.DistanceInterval=.001;sim=New();Seek(sim,DirectorCreationParticleSimulation.Step);
        Check(sim.Particles.Count==8,"Distance emission lost multiple crossings in one tick");
        emitter.Modules.DistanceInterval=0;emitter.Burst=true;emitter.Properties["LifeLength"]=CreationParticleFields.Nat(.1);
        var ribbon=new CreationParticleEmitter{Duration=2,Modules=new(){Geometry=ParticleGeometry.Trail,TrailEmitter=emitter.Id,SampleDistance=.001,Retain=.5}};
        effect.Emitters.Add(ribbon);emitter.Properties["Velocity"]=new JsonArray(CreationParticleFields.Nat(1),CreationParticleFields.Nat(0),CreationParticleFields.Nat(0));
        sim=New();Seek(sim,.3);Check(sim.Ribbons.Count==1&&sim.Ribbons[0].Points.Length>2,"Particle trail missing");
        string trailFrame=JsonSerializer.Serialize(sim.Ribbons);Seek(sim,.6);Check(sim.Ribbons.Count==1,"Trail disappeared immediately when source particle died");Seek(sim,.3);
        Check(trailFrame==JsonSerializer.Serialize(sim.Ribbons),"Particle trail rewind mismatch");Seek(sim,1.1);Check(sim.Ribbons.Count==0,"Expired particle trail remained");
        var cloned=effect.Duplicate();Check(cloned.Emitters[1].Modules!.TrailEmitter==cloned.Emitters[0].Id,"Preset duplicate broke trail binding");
        effect.Emitters.Remove(ribbon);effect.GameSpeed=4;sim=New();Seek(sim,.07);Check(sim.Particles.Count==0,"Native game speed lifetime scaling missing");effect.GameSpeed=1;
        emitter.Modules.MeshParticle=true;emitter.Modules.Mesh=[new(0,0,0),new(1,0,0),new(0,1,0)];sim=New();Seek(sim,0);Check(sim.Particles[0].Mesh?.Count==3,"Static particle mesh missing");
        string libraryRoot=Path.Combine(Path.GetTempPath(),"director-particle-library-"+Guid.NewGuid().ToString("N"));
        try
        {
            var library=new DirectorParticlePresetLibrary(libraryRoot);var entry=new ParticleLibraryPreset{Name="中文 smoke",Tags="fire 风",Favorite=true,Effect=effect};
            library.Save(entry);var saved=library.List().Single();Check(saved.Name==entry.Name&&saved.Tags==entry.Tags&&saved.Favorite&&saved.Effect.Emitters[0].Modules!.Mesh.Count==3,"Library round trip lost metadata or modules");
            library.Delete(entry.Id);Check(library.List().Count==0,"Library delete failed");
        }
        finally{if(Directory.Exists(libraryRoot))Directory.Delete(libraryRoot,true);}
        bool rejectedNull=false;var malformed=CreationParticleEffect.Create();malformed.Emitters[0].Modules=null;
        try{malformed.Validate();}catch(InvalidDataException){rejectedNull=true;}Check(rejectedNull,"Null v3 modules accepted");
        var uv=DirectorCreationParticleSimulation.ParticleUv(new(){Columns=2,Rows=2,Frames=4,FramesPerSecond=1},2,5);Check(uv==new Vector4(0,.5f,.5f,1),"Flipbook UV wrong");
    }
}
