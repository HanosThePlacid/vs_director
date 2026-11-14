using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using VSDirector.Data;

internal static class CreationAdvancedParticleChecks
{
    private static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
    private static (DirectorCreationProject Project,CreationSceneObject Object,CreationParticleEmitter Emitter) Scene()
    {
        var obj=new CreationSceneObject{Kind=CreationObjectKind.Particle,ParticleDuration=2,ParticleEffect=new()};
        var e=obj.ParticleEffect.Emitters[0];e.Burst=true;
        e.Properties["GravityEffect"]=CreationParticleFields.Nat(0);e.Properties["TerrainCollision"]=false;
        return(new(){Objects=[obj]},obj,e);
    }
    private static DirectorCreationParticleSimulation Sim(DirectorCreationProject p,ICreationParticleEnvironment? env=null,Func<Guid,double,Matrix4x4>? tr=null)
        =>new(p,tr??((_,_)=>Matrix4x4.Identity),env);
    private static CreationSimulatedParticle[] At(DirectorCreationParticleSimulation sim,double time)
    {int batches=0;while(!sim.Seek(time,37)){if(++batches>10000)throw new Exception("Rebuild stalled");}return sim.Particles.ToArray();}
    internal static void Run()
    {
        var (project,obj,e)=Scene();
        foreach(string dist in CreationParticleFields.Distributions)
        foreach(string fn in CreationParticleFields.Transforms)
        {
            var p=CreationParticleFields.Defaults();
            foreach(var field in CreationParticleFields.All)
            {
                JsonNode? Value()=>field.Kind switch
                {"nat"=>new JsonObject{["avg"]=2,["var"]=.4,["offset"]=.1,["dist"]=dist},"evolve"=>new JsonObject{["factor"]=.2,["transform"]=fn,["maxvalue"]=200},_=>null};
                if(field.Kind is "nat" or "evolve")p[field.Name]=field.Length>0?new JsonArray(Enumerable.Range(0,field.Length).Select(_=>Value()).ToArray()):Value();
            }
            p["SecondaryParticles"]=new JsonArray(CreationParticleFields.Defaults());p["DeathParticles"]=new JsonArray(CreationParticleFields.Defaults());p["future"]=new JsonObject{["untouched"]=42};
            e.Properties=p;
            var copy=CreationParticleFields.Import(JsonSerializer.Serialize(obj.ParticleEffect,CreationJson.Options));
            Check(JsonNode.DeepEquals(copy.Emitters[0].Properties,p),"Parameter round trip dropped a field");
            var native=CreationParticleFields.Import(CreationParticleFields.ExportNative(obj.ParticleEffect!));
            Check(CreationParticleFields.Find(native.Emitters[0].Properties,"future") is null,"Native export leaked unsupported fields");
            Check(CreationParticleFields.Find(p,"future") is not null,"Native export modified stored fields");
        }
        Check(CreationParticleFields.Import("[{},{}]").Emitters.Count==2,"Native emitter array import failed");
        Check(CreationJson.Clone(new CreationSceneObject{Kind=CreationObjectKind.Particle}).ParticleEffect is null,"Legacy object auto-migrated");
        (project,obj,e)=Scene();e.Start=obj.ParticleDuration+.1;
        Check(At(Sim(project),3).Length==0,"Burst emitted after the object's emission span");
        e.Start=obj.ParticleDuration=0;
        Check(At(Sim(project),0).Length==1,"Zero-duration instantaneous burst was lost");
        (project,obj,e)=Scene();e.Burst=false;e.Interval=.07;e.Duration=.5;
        e.Properties["Velocity"]=new JsonArray(CreationParticleFields.Nat(1,.2),CreationParticleFields.Nat(2,.3),CreationParticleFields.Nat(0));
        var sim=Sim(project);var expected=At(sim,2);
        At(sim,4);Check(expected.SequenceEqual(At(sim,2)),"Backward seek changed particle state");
        At(sim,0);Check(expected.SequenceEqual(At(sim,2)),"Loop reset changed particle state");
        foreach(int fps in new[]{24,30,60,120})
        {var export=Sim(project);for(int frame=0;frame<=fps*2;frame++)At(export,frame/(double)fps);Check(expected.SequenceEqual(export.Particles),"Export FPS changed simulation");}
        (project,obj,e)=Scene();e.Properties["Quantity"]=new JsonObject{["avg"]=2,["var"]=0,["dist"]="dirac"};e.Burst=false;e.Duration=2;
        sim=Sim(project);Check(At(sim,1).Length==2,"Dirac repeated its impulse");At(sim,0);Check(At(sim,1).Length==2,"Dirac state not restored on rewind");
        (project,obj,e)=Scene();e.Properties["LifeLength"]=CreationParticleFields.Nat(.1);
        sim=Sim(project);Check(At(sim,.4).Length==1&&At(sim,.5).Length==0,"Native lifetime units/expiry incorrect");
        var child=CreationParticleFields.Defaults();child["GravityEffect"]=CreationParticleFields.Nat(0);child["TerrainCollision"]=false;child["SecondarySpawnInterval"]=CreationParticleFields.Nat(.1);
        e.Properties["SecondaryParticles"]=new JsonArray(child.DeepClone());e.Properties["DeathParticles"]=new JsonArray(child.DeepClone());
        sim=Sim(project);Check(At(sim,.21).Length==3,"Secondary interval must be read from child definition");Check(At(sim,.5).Any(p=>p.Index>1),"Death particles missing");
        (project,obj,e)=Scene();e.Properties["Velocity"]=new JsonArray(CreationParticleFields.Nat(1),CreationParticleFields.Nat(0),CreationParticleFields.Nat(0));
        sim=Sim(project,tr:(_,t)=>Matrix4x4.CreateTranslation((float)t*10,0,0));
        Check(Math.Abs(At(sim,1)[0].Position.X-1)<1e-5,"World particles dragged with moving emitter");
        e.LocalFollow=true;sim=Sim(project,tr:(_,t)=>Matrix4x4.CreateTranslation((float)t*10,0,0));
        Check(Math.Abs(At(sim,1)[0].Position.X-11)<1e-5,"Explicit local follow ignored");
        e.LocalFollow=false;e.ParentVelocityWeight=.5;e.Start=.1;
        sim=Sim(project,tr:(_,t)=>Matrix4x4.CreateTranslation((float)t*10,0,0));
        Check(At(sim,1)[0].Position.X>6,"Parent velocity not inherited");
        (project,obj,e)=Scene();e.Properties["WindAffectednes"]=1;
        sim=Sim(project,new Env(wind:new(2,0,0)));Check(Math.Abs(At(sim,1)[0].Position.X-2)<1e-5,"Wind ignored");
        e.Properties["DieInLiquid"]=true;sim=Sim(project,new Env(liquid:true));Check(At(sim,.1).Length==0,"Liquid death ignored");
        e.Properties["DieInLiquid"]=false;e.Properties["DieInAir"]=true;sim=Sim(project);Check(At(sim,.1).Length==0,"Air death ignored");
        (project,obj,e)=Scene();e.Offset=new(0,.75,0);e.Properties["TerrainCollision"]=true;e.Properties["Bounciness"]=1;
        e.Properties["Velocity"]=new JsonArray(CreationParticleFields.Nat(0),CreationParticleFields.Nat(-2),CreationParticleFields.Nat(0));
        sim=Sim(project,new Env(floor:true));double low=At(sim,.12)[0].Position.Y;Check(At(sim,.3)[0].Position.Y>low,"Collision box bounce ignored");
        var aligned=At(sim,.3)[0].Position;
        sim=Sim(project,new Env(floor:true,origin:new(.35,.6,.45)),(_,_)=>Matrix4x4.CreateTranslation(-.35f,-.6f,-.45f));
        Check(Vector3.Distance((At(sim,.3)[0].Position+new CreationVector(.35,.6,.45)).Float(),aligned.Float())<1e-5,"Fractional project origin moved native collision boxes");
        (project,obj,e)=Scene();e.Properties["ColorByBlock"]=true;e.Properties["SizeEvolve"]=CreationParticleFields.Evolution("LINEAR",4);
        e.Properties["OpacityEvolve"]=CreationParticleFields.Evolution("LINEAR",-255);e.Properties["RedEvolve"]=CreationParticleFields.Evolution("LINEAR",255);
        sim=Sim(project,new Env(color:new(.2,.3,.4)));var evolved=At(sim,2.5)[0];
        Check(Math.Abs(evolved.Size-.75)<1e-6&&Math.Abs(evolved.Alpha-.5)<1e-6&&Math.Abs(evolved.Color.X-.9)<1e-6,"Size/opacity/RGB evolution or block color ignored");
        e.Properties["DieOnRainHeightmap"]=true;sim=Sim(project,new Env(rain:1));Check(At(sim,.1).Length==0,"Rain heightmap death ignored");
        e.Properties["DieOnRainHeightmap"]=false;e.Properties["SwimOnLiquid"]=true;e.Offset=new(0,.2,0);
        e.Properties["Velocity"]=new JsonArray(CreationParticleFields.Nat(0),CreationParticleFields.Nat(-1),CreationParticleFields.Nat(0));
        sim=Sim(project,new Env(liquid:true));Check(At(sim,.1)[0].Position.Y>=1,"Liquid surface swimming ignored");
        (project,obj,e)=Scene();var bone=new CreationPart{Group=true,From=new(0,16,0)};var model=new CreationModel{Parts=[bone]};
        var parent=new CreationSceneObject{ModelId=model.Id,Transform=new(){Position=new(2,0,0)}};
        project.Models.Add(model);project.Objects.Add(parent);obj.ParentId=parent.Id;obj.ParentPartId=bone.Id;e.Start=.5;
        DirectorCreationEdits.SetKey(project,parent.Id,bone.Id,CreationChannel.Position,0,new(0,0,0));
        DirectorCreationEdits.SetKey(project,parent.Id,bone.Id,CreationChannel.Position,1,new(1,0,0));
        sim=Sim(project,tr:(id,t)=>DirectorCreationEvaluator.Evaluate(project,t).Objects[id].World);
        var atSpawn=DirectorCreationEvaluator.Evaluate(project,.5).Objects[obj.Id].World.Translation;
        Check(Vector3.Distance(At(sim,1)[0].Position.Float(),atSpawn)<1e-5,"Bone emitter used the current transform instead of the emission transform");
        (project,obj,e)=Scene();e.Properties["Quantity"]=CreationParticleFields.Nat(6001);
        sim=Sim(project);bool budget=false;try{At(sim,0);}catch(CreationParticleBudgetException ex){budget=ex.Message.Contains(obj.Name);}
        Check(budget&&sim.Error.Length>0&&!sim.Seek(1),"Budget overflow was silently dropped or failed to latch");
        (project,obj,e)=Scene();e.Properties["Quantity"]=CreationParticleFields.Nat(6000);
        for(int i=0;i<4;i++){var another=CreationJson.Clone(obj);another.Id=Guid.NewGuid();project.Objects.Add(another);}
        sim=Sim(project);budget=false;try{At(sim,0);}catch(CreationParticleBudgetException){budget=true;}Check(budget,"Project particle budget ignored");
        var nested=CreationParticleFields.Defaults();var leaf=nested;
        for(int i=0;i<9;i++){var next=CreationParticleFields.Defaults();leaf["DeathParticles"]=new JsonArray(next);leaf=next;}
        bool depth=false;try{CreationParticleFields.Validate(nested);}catch(InvalidDataException){depth=true;}Check(depth,"Particle recursion limit ignored");
        (project,obj,e)=Scene();sim=Sim(project);Check(!sim.Seek(20,10)&&sim.Progress<1,"Long seek did not yield");sim.Cancel();Check(!sim.Seek(20)&&!sim.Ready,"Cancellation ignored");
        var legacy=new CreationSceneObject{Kind=CreationObjectKind.Particle,ParticleStart=.3,ParticleDuration=1,ParticleCount=7,ParticleLife=2,Color=new(.8,.4,.2),ParticleSettings=new(){Seed=8}};
        obj=CreationJson.Clone(legacy);obj.ParticleEffect=CreationParticleFields.ConvertLegacy(legacy);project=new(){Objects=[obj]};sim=Sim(project);
        foreach(double time in new[]{0d,.3,.8,1.5,3.5})
        {
            var old=DirectorCreationParticles.Sample(legacy,time).ToArray();var advanced=At(sim,time);
            Check(old.Length==advanced.Length,"Explicit legacy conversion changed emission timing");
            for(int i=0;i<old.Length;i++)Check(Vector3.Distance(old[i].Position.Float(),advanced[i].Position.Float())<1e-6&&old[i].Size==advanced[i].Size&&old[i].Alpha==advanced[i].Alpha,"Legacy visual changed on conversion");
        }
        sim=Sim(project,tr:(_,_)=>Matrix4x4.CreateScale(2,3,4)*Matrix4x4.CreateRotationY(.4f));
        var scaled=At(sim,.8)[0];Check(Vector3.Distance(scaled.ShapeScale.Float(),new(2,3,4))<1e-5&&scaled.ShapeRotation!=Quaternion.Identity,"Legacy conversion lost scale/cube orientation");
        e.Texture="assets/advanced.png";project=new(){Objects=[new(){Kind=CreationObjectKind.Particle,ParticleEffect=new(){Emitters=[e]}}]};
        Check(DirectorCreationDependencies.Collect(project).Any(d=>d.Source==e.Texture),"Advanced texture dependency missing");
    }
    private sealed class Env(bool liquid=false,bool floor=false,CreationVector wind=default,CreationVector origin=default,CreationVector? color=null,double rain=double.NegativeInfinity):ICreationParticleEnvironment
    {
        public CreationVector Wind=>wind;
        public CreationVector GridOrigin=>origin;
        public CreationParticleCell Cell(int x,int y,int z)=>new(floor&&y==0,liquid,rain,color??CreationVector.One,
            floor&&y==0?[new(new(0,0,0),new(1,.4,1))]:[]);
    }
}
