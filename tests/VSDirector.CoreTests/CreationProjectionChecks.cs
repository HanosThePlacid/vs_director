using System.Numerics;
using System.Text.Json;
using VSDirector.Data;

internal static class CreationProjectionChecks
{
    internal static void Run()
    {
        static void Check(bool ok,string message){if(!ok)throw new Exception(message);}
        var root=new CreationSceneObject{Kind=CreationObjectKind.Structure,Projection=new(){Size=new(4,3,2),Anchor=new(1,0,1),Author="Builder",View=new(){Width=320,Height=240,Fps=24,Duration=2.5}},Transform=new(){Position=new(3,0,2)}};
        var effect=new CreationSceneObject{Kind=CreationObjectKind.Particle,ParentId=root.Id,ProjectionParticleSource="torch",ParticleEffect=CreationParticleEffect.Create()};
        effect.ParticleEffect.Emitters[0].Burst=true;
        var project=new DirectorCreationProject{Objects=[root,effect]};project.Validate();
        var roundtrip=JsonSerializer.Deserialize<DirectorCreationProject>(JsonSerializer.Serialize(project,CreationJson.Options),CreationJson.Options)!;
        roundtrip.Validate();var copy=roundtrip.Objects[0];
        Check(copy.Projection!.Author=="Builder"&&copy.Projection.Anchor.X==1&&copy.Projection.View.Frames==60,"Projection metadata or export settings did not round-trip");
        Check(roundtrip.Objects[1].ParentId==copy.Id&&roundtrip.Objects[1].ProjectionParticleSource=="torch","Projection particle association was lost");
        var sim=new DirectorCreationParticleSimulation(roundtrip,(_,_)=>Matrix4x4.Identity);
        Check(sim.Seek(0)&&sim.Particles.Count>0,"Automatic projection particles did not emit");
        copy.Projection.AutomaticParticles=false;
        sim=new(roundtrip,(_,_)=>Matrix4x4.Identity);
        Check(sim.Seek(0)&&sim.Particles.Count==0,"Disabled automatic projection particles still emitted");
        roundtrip.Objects[1].ProjectionParticleSource="";
        sim=new(roundtrip,(_,_)=>Matrix4x4.Identity);
        Check(sim.Seek(0)&&sim.Particles.Count>0,"Disabling automatic particles also disabled manual children");
        Check(new CreationSceneObject{Kind=CreationObjectKind.Structure}.Projection is null,"Legacy structures were implicitly converted");
        bool rejected=false;
        try{new CreationProjectionView{Duration=120,Fps=60}.Validate();}catch(InvalidDataException){rejected=true;}
        Check(rejected,"Excessive animation frame count accepted");
        rejected=false;
        try{new CreationProjectionView{Yaw=double.NaN}.Validate();}catch(InvalidDataException){rejected=true;}
        Check(rejected,"Non-finite projection camera accepted");
    }
}
