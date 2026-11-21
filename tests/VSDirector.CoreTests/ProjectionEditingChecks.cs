using System.Numerics;
using VSDirector.Data;

internal static class ProjectionEditingChecks
{
    internal static void Run()
    {
        static void Check(bool value,string text){if(!value)throw new Exception(text);}
        static void Near(double a,double b,string text){Check(Math.Abs(a-b)<1e-5,text+$" ({a}, {b})");}
        ProjectionCell[] source=[new(0,0,new(0,0,0),"game:stone"),new(1,1,new(0,1,0),"game:stone"),new(2,2,new(1,0,0),"game:glass"),new(3,3,new(0,0,0),"game:water",true)];
        var s=new CreationProjectionSettings{Version=2,Size=new(2,2,1)};
        s.Validate();var original=DirectorProjectionEvaluator.Evaluate(source,s,0);
        Check(original.All(c=>c.Visible)&&original.Count==4,"Unedited structure changed visibility or lost liquid layer");
        Near(Vector3.Transform(Vector3.Zero,original[1].Matrix).Y,1,"Local cell placement changed");
        s.Edit(source[0]).Offset=new(3,0,0);s.Edit(source[0]).Scale=new(2,2,2);
        var changed=DirectorProjectionEvaluator.Evaluate(source,s,0)[0];Near(Vector3.Transform(new Vector3(.5f),changed.Matrix).X,3.5,"Scaled cell moved its center");
        var copies=DirectorProjectionEditing.Duplicate(source,s,new HashSet<int>{0,1});Check(copies.Count==2&&copies.All(i=>i<0),"Copy IDs are not independent");
        var copyCells=DirectorProjectionEditing.Cells(source,s).Where(c=>copies.Contains(c.Id)).ToArray();
        var a=DirectorProjectionEditing.Position(copyCells[0],s)-DirectorProjectionEditing.Position(source[0],s);
        var b=DirectorProjectionEditing.Position(copyCells[1],s)-DirectorProjectionEditing.Position(source[1],s);Check(a==b,"Duplicate group did not preserve internal offset");
        s.RangeMin=new(0,0,0);s.RangeMax=new(0,1,0);Check(DirectorProjectionEditing.InRange(source[1],s)&&!DirectorProjectionEditing.InRange(source[2],s),"Split range uses wrong coordinates");
        s=new(){Version=2,Size=new(2,2,1)};s.Parameter("growth").Value=.5;
        var growing=DirectorProjectionEvaluator.Evaluate(source,s,0);Check(growing[0].Visible&&!growing[1].Visible,"Bottom-up growth order failed");
        s.Parameter("growth").Value=1;s.Parameter("waveAEnabled").Value=1;s.Parameter("waveADirection").Value=0;s.Parameter("waveA").Value=.5;
        var wave=DirectorProjectionEvaluator.Evaluate(source,s,0);Check(wave[0].Visible&&!wave[2].Visible,"Positive-X wave failed");
        s.Parameter("waveBEnabled").Value=1;s.Parameter("waveBDirection").Value=1;s.Parameter("waveB").Value=.5;
        Check(DirectorProjectionEvaluator.Evaluate(source,s,0).All(c=>c.Visible),"Wave union failed");
        s.Parameter("fill").Value=0;Check(DirectorProjectionEvaluator.Evaluate(source,s,0).All(c=>!c.Visible),"Zero fill left visible cells");s.Parameter("fill").Value=1;
        s.Glass[0].Value="game:glass-a";s.Glass[4].Value="game:glass-b";s.Parameter("glass0").Value=s.Parameter("glass4").Value=1;
        Check(DirectorProjectionEvaluator.Evaluate(source,s,0).All(c=>c.Code=="game:glass-b"),"Higher glass layer did not win");
        s.Parameter("blockEdge").Value=1;s.Parameter("edge3").Value=1;s.Edges[3].Value="game:edge";
        Check(DirectorProjectionEvaluator.Evaluate(source,s,0).All(c=>c.Code=="game:edge"),"Edge replacement did not override glass");
        s.Edges[3].Keys.Add(new(){Time=1,Value="game:edge-new"});Check(DirectorProjectionEvaluator.Evaluate(source,s,1).All(c=>c.Code=="game:edge-new"),"Material step key failed");
        s=new(){Version=2,Size=new(2,2,1)};var e=s.Edit(source[0]);e.Offset=new(2,0,0);
        e.Tracks.Add(new(){Channel=CreationChannel.Position,Keys=[new(){Time=0,Value=default},new(){Time=1,Value=new(2,0,0)}]});
        Near(DirectorProjectionEvaluator.Evaluate(source,s,1)[0].Matrix.Translation.X,2,"Disabled cell tracks still applied");
        s.CellKeyframes=true;Near(DirectorProjectionEvaluator.Evaluate(source,s,1)[0].Matrix.Translation.X,4,"Additive cell track failed");
        e.OverrideTransform=true;Near(DirectorProjectionEvaluator.Evaluate(source,s,1)[0].Matrix.Translation.X,2,"Absolute cell track failed");
        s.CellKeyframes=false;s.CellKeyframes=true;Check(e.Tracks[0].Keys.Count==2,"Track switch removed keyframes");
        var roundtrip=CreationJson.Clone(s);roundtrip.Validate();Check(roundtrip.Edits[0].Tracks[0].Keys.Count==2,"Cell keyframes lost on persistence");
        for(int i=0;i<120;i++)
        {
            var p=DirectorProjectionEditing.Edge(i,120,-.25);int boundaries=(p.X is 0 or 1?1:0)+(p.Y is 0 or 1?1:0)+(p.Z is 0 or 1?1:0);
            Check(boundaries>=2&&p.X>=0&&p.X<=1&&p.Y>=0&&p.Y<=1&&p.Z>=0&&p.Z<=1,"Edge point left cube edges");
        }
        Physics();Particles();
    }
    internal static void Persistence()
    {
        var root=new CreationSceneObject{Kind=CreationObjectKind.Structure,Projection=new(){Version=2,Size=CreationVector.One}};
        var s=root.Projection!;
        foreach(var f in ProjectionFields.All){var p=s.Parameter(f.Id);p.Value=f.Min;p.Track.Keys=[new(){Time=1,Value=new(f.Max,0,0)}];}
        s.ParticleSchemes["custom"]=CreationParticleEffect.Create();s.ParticleScheme.Value="custom";
        var emitter=s.ParticleSchemes["custom"].Emitters[0];emitter.Texture="assets/edge.png";emitter.Modules=new(){BindObject=root.Id,EndObject=root.Id};
        s.Edit(new(0,0,default,"game:stone")).Tracks.Add(new(){Channel=CreationChannel.Position,Keys=[new(){Time=2,Value=CreationVector.One}]});
        s.CellKeyframes=true;
        var project=new DirectorCreationProject{Objects=[root]};project.Validate();
        var copy=CreationJson.Clone(project);copy.Validate();
        foreach(var f in ProjectionFields.All)if(copy.Objects[0].Projection!.V(f.Id,1)!=f.Max)throw new Exception("Projection field roundtrip failed: "+f.Id);
        var duplicate=DirectorCreationEdits.Duplicate(project);duplicate.Validate();
        var dupe=duplicate.Objects[0];var node=dupe.Projection!.ParticleSchemes["custom"].Emitters[0];
        if(node.Id==emitter.Id||node.Modules!.BindObject!=dupe.Id||node.Modules.EndObject!=dupe.Id)throw new Exception("Duplicated structure retained original binding identities");
        if(dupe.Projection.Edits[0].Tracks[0].Id==s.Edits[0].Tracks[0].Id)throw new Exception("Duplicated cell track retained identity");
        if(!DirectorCreationDependencies.Collect(project).Any(d=>d.Source=="assets/edge.png"))throw new Exception("Structure particle texture missing from resource checks");
        var old=new CreationProjectionSettings{Size=CreationVector.One};old.Validate();if(CreationJson.Clone(old).Version!=1)throw new Exception("Old structure automatically migrated");
    }
    internal static void LargeStructure()
    {
        var source=Enumerable.Range(0,100000).Select(i=>new ProjectionCell(i,i,new(i%100,i/10000,(i/100)%100),"game:stone")).ToArray();
        var s=new CreationProjectionSettings{Version=2,Size=new(100,10,100)};
        var simulation=new DirectorProjectionSimulation(source,s);int slices=0;while(!simulation.Seek(0)){if(++slices>10000)throw new Exception("Large structure rebuild never finished");}
        if(simulation.Frame.Count!=source.Length)throw new Exception("Large structure lost cells");
        var frame=simulation.Frame;if(!simulation.Seek(10)||!ReferenceEquals(frame,simulation.Frame))throw new Exception("Static structure rebuilt at every timeline position");
        s.Parameter("physicsEnabled").Value=1;simulation=new(source,s);bool rejected=false;
        try{while(!simulation.Seek(1)){}}catch(InvalidDataException){rejected=true;}
        if(!rejected)throw new Exception("Large structure bypassed physics limit");
    }
    internal static void AutomaticParticles()
    {
        var root=new CreationSceneObject{Kind=CreationObjectKind.Structure,Projection=new(){Version=2,Size=CreationVector.One}};
        var effect=CreationParticleEffect.Create();effect.Emitters[0].Burst=true;
        CreationParticleFields.Set(effect.Emitters[0].Properties,"GravityEffect",CreationParticleFields.Nat(0));
        CreationParticleFields.Set(effect.Emitters[0].Properties,"Velocity",new System.Text.Json.Nodes.JsonArray(CreationParticleFields.Nat(0),CreationParticleFields.Nat(0),CreationParticleFields.Nat(0)));
        var child=new CreationSceneObject{Kind=CreationObjectKind.Particle,ParentId=root.Id,ProjectionParticleSource="0:game:fire",ParticleDuration=10,ParticleEffect=effect};
        var project=new DirectorCreationProject{Objects=[root,child]};var cell=new ProjectionCell(0,0,default,"game:fire");
        CreationSimulatedParticle[] Sample()
        {
            var simulation=new DirectorCreationParticleSimulation(project,(_,_)=>Matrix4x4.Identity);simulation.AddProjection(new(root,[cell]));
            while(!simulation.Seek(0)){}return simulation.Particles.ToArray();
        }
        var before=Sample();root.Projection!.Edit(cell).Offset=new(5,2,1);var moved=Sample();
        if(before.Length==0||before.Length!=moved.Length)throw new Exception("Automatic particle source disappeared");
        for(int i=0;i<before.Length;i++)if((moved[i].Position-before[i].Position-new CreationVector(5,2,1)).Float().Length()>1e-5)throw new Exception("Automatic particles did not follow cell edits");
        root.Projection.Edit(cell).Deleted=true;if(Sample().Length!=0)throw new Exception("Deleted block kept emitting automatic particles");
        root.Projection.Edits[-1]=new(){Source=0,Offset=new(7,0,0)};child.ProjectionParticleSource="cell:-1";
        var copied=Sample();if(copied.Length!=before.Length||Math.Abs(copied[0].Position.X-before[0].Position.X-7)>1e-5)throw new Exception("Copied automatic particle source did not follow its independent cell");
    }
    private static void Physics()
    {
        var source=new[]{new ProjectionCell(0,0,new(0,3,0),"game:stone")};var settings=new CreationProjectionSettings{Version=2,Size=new(1,4,1)};
        settings.Parameter("physicsEnabled").Value=1;settings.Parameter("spin").Value=20;
        var direct=new DirectorProjectionSimulation(source,settings,new Floor());while(!direct.Seek(2)){}
        var matrix=direct.Frame.Single().Matrix;
        var stepped=new DirectorProjectionSimulation(source,settings,new Floor());for(int i=0;i<=120;i++)while(!stepped.Seek(i/60d)){}
        if(matrix!=stepped.Frame.Single().Matrix)throw new Exception("Physics differs with presentation FPS");
        while(!direct.Seek(.25)){}while(!direct.Seek(2)){}if(matrix!=direct.Frame.Single().Matrix)throw new Exception("Physics rewind did not reproduce state");
        if(Vector3.Transform(new Vector3(.5f),matrix).Y<.4)throw new Exception("Falling cell passed through floor");
        direct.Cancel();if(direct.Seek(3))throw new Exception("Cancelled simulation advanced");
        settings.Parameter("physicsEnabled").Track.Keys.Add(new(){Time=.5,Value=default,Interpolation=CreationInterpolation.Hold});
        var off=new DirectorProjectionSimulation(source,settings,new Floor());while(!off.Seek(1)){}if(Math.Abs(off.Frame[0].Matrix.Translation.Y-3)>1e-5)throw new Exception("Physics off did not restore authored state");
    }
    private sealed class Floor : ICreationParticleEnvironment
    {
        public CreationVector Wind=>default;
        public CreationParticleCell Cell(int x,int y,int z)=>new(y<0,false,0,CreationVector.One);
    }
    private static void Particles()
    {
        var root=new CreationSceneObject{Kind=CreationObjectKind.Structure,Projection=new(){Version=2,Size=CreationVector.One}};var settings=root.Projection;
        settings.Parameter("particlesEnabled").Value=1;settings.Parameter("particleRatio").Value=1;settings.Parameter("particleCount").Value=12;
        settings.Parameter("particleOverride").Value=1;settings.Parameter("particleLife").Value=2;settings.Parameter("particleInterval").Value=1;
        var effect=CreationParticleEffect.Create();CreationParticleFields.Set(effect.Emitters[0].Properties,"GravityEffect",CreationParticleFields.Nat(0));
        settings.ParticleSchemes["probe"]=effect;settings.ParticleScheme.Value="probe";
        var project=new DirectorCreationProject{Objects=[root]};project.Validate();
        var simulation=new DirectorCreationParticleSimulation(project,(_,_)=>Matrix4x4.Identity);simulation.AddProjection(new(root,[new(0,0,default,"game:stone")]));
        while(!simulation.Seek(0)){}if(simulation.Particles.Count!=12)throw new Exception("Edge particle count ignored");
        while(!simulation.Seek(.2)){}if(simulation.Particles.Count!=0)throw new Exception("Tick lifetime override ignored");
        while(!simulation.Seek(0)){}if(simulation.Particles.Count!=12)throw new Exception("Projection particle rewind lost emissions");
        settings.Parameter("outline").Value=1;
        simulation=new(project,(_,_)=>Matrix4x4.Identity);simulation.AddProjection(new(root,[new(0,0,default,"game:stone")]));while(!simulation.Seek(0)){}
        if(simulation.Particles.Count!=24)throw new Exception("Projection particle outlines absent");
        settings.Parameter("particleInterval").Value=.2;settings.Parameter("particleLife").Value=20;
        var a=new DirectorCreationParticleSimulation(project,(_,_)=>Matrix4x4.Identity);a.AddProjection(new(root,[new(0,0,default,"game:stone")]));
        var b=new DirectorCreationParticleSimulation(project,(_,_)=>Matrix4x4.Identity);b.AddProjection(new(root,[new(0,0,default,"game:stone")]));
        for(int i=0;i<=48;i++)while(!a.Seek(i/24d)){}
        for(int i=0;i<=120;i++)while(!b.Seek(i/60d)){}
        if(!a.Particles.SequenceEqual(b.Particles))throw new Exception("Structure particles changed between 24 and 60 FPS");
    }
}
