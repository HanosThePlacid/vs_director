using System.Numerics;

namespace VSDirector.Data;

public sealed record CreationParticleRibbon(Guid ObjectId,Guid EmitterId,CreationVector[] Points,double Width,CreationVector Color,double Alpha,
    string Texture,bool Additive,double Glow,double Flow, double[]? Widths=null, CreationVector[]? Colors=null, double[]? Alphas=null);

public sealed partial class DirectorCreationParticleSimulation
{
    private sealed record Schedule(double Next, long Sequence, double Distance, Vector3 Position, double Time);
    private readonly Dictionary<(Guid Object,Guid Emitter),Schedule> moduleSchedules=new();
    private readonly Dictionary<long,Dictionary<(Guid Object,Guid Emitter),Schedule>> moduleSnapshots=new();
    private sealed record TrailSample(double Time,CreationVector Position);
    private sealed record TrailTrack(Guid Object,Guid Source,Guid Trail,List<TrailSample> Points);
    private Dictionary<(Guid,long),TrailTrack> particleTrails=new();
    private readonly Dictionary<long,Dictionary<(Guid,long),TrailTrack>> trailSnapshots=new();
    private static Dictionary<(Guid,long),TrailTrack> CopyTrails(Dictionary<(Guid,long),TrailTrack> source)
        =>source.ToDictionary(p=>p.Key,p=>p.Value with {Points=new(p.Value.Points)});
    private void RecordTrails(double now)
    {
        foreach(var (obj,e,_) in roots.Where(r=>r.Emitter.Modules is {Geometry:ParticleGeometry.Trail,TrailEmitter:var id}&&id!=Guid.Empty))
        {
            var m=e.Modules!;double stop=obj.ParticleStart+Math.Min(obj.ParticleDuration,e.Start+e.Duration);
            foreach(var pair in particleTrails.Where(p=>p.Value.Trail==e.Id&&p.Value.Object==obj.Id).ToArray())
            {pair.Value.Points.RemoveAll(p=>p.Time<now-m.Retain);if(pair.Value.Points.Count==0)particleTrails.Remove(pair.Key);}
            if(now<obj.ParticleStart+e.Start||now>stop)continue;
            foreach(var p in live.Where(p=>p.Object.Id==obj.Id&&p.Emitter.Id==m.TrailEmitter&&p.Born<=now))
            {
                var key=(e.Id,p.Id);if(!particleTrails.TryGetValue(key,out var track))particleTrails[key]=track=new(obj.Id,p.Emitter.Id,e.Id,[]);
                if(track.Points.Count==0||Vector3.Distance(track.Points[^1].Position.Float(),p.Position.Float())>=m.SampleDistance)track.Points.Add(new(now,p.Position));
            }
        }
        int total=0;foreach(var group in particleTrails.Values.GroupBy(t=>t.Object))
        {int vertices=group.Sum(t=>t.Points.Count*2);total+=vertices;if(vertices>EffectVertexBudget||total>ProjectVertexBudget)throw new CreationParticleBudgetException(project.Objects.First(o=>o.Id==group.Key).Name+": particle trail vertex budget exceeded.");}
    }
    public IReadOnlyList<CreationParticleRibbon> Ribbons {get;private set;}=[];
    public Func<Guid,Guid,double,Matrix4x4>? Attachment {get;set;}
    public const int EffectVertexBudget=16384,ProjectVertexBudget=65536;
    private Matrix4x4 EmitterMatrix(CreationSceneObject obj,CreationParticleEmitter e,double time)
        =>e.Modules is {BindObject:var id,BindBone:var bone} && id!=Guid.Empty
            ?Attachment?.Invoke(id,bone,time)??transform(id,time):transform(obj.Id,time);
    private void EmitModuleRoot(CreationSceneObject obj,CreationParticleEmitter e,Definition d,double now)
    {
        var m=e.Modules!;if(m.Geometry==ParticleGeometry.Trail)return;
        double start=obj.ParticleStart+e.Start;
        if(now<start || e.Start>obj.ParticleDuration)return;
        double stop=obj.ParticleStart+obj.ParticleDuration;
        if(!moduleSchedules.TryGetValue((obj.Id,e.Id),out var state))
            state=new(start+(m.Bursts.Count>0?m.Bursts.Min():0),0,0,EmitterMatrix(obj,e,start).Translation,start);
        int emitted=0;
        void Emit(double born,long sequence)
        {
            if(++emitted>4096)throw new CreationParticleBudgetException(obj.Name+" / "+e.Name+": emission work budget exceeded.");
            if(e.Recipe==CreationParticleRecipe.ReferenceFire)
            {
                var selection=new Rng(Seed(obj.ParticleEffect!.Seed,sequence,918));
                int choice=Math.Min(e.ProviderCount-1,(int)(selection.Next()*(e.ProviderCount+1)));
                if(e.ProviderIndex!=choice)return;
            }
            var matrix=EmitterMatrix(obj,e,born);var prev=EmitterMatrix(obj,e,Math.Max(0,born-Step));
            var pos=CreationVector.From(Vector3.Transform(e.Offset.Float(),matrix));
            if(m.Geometry==ParticleGeometry.Particle)
                Spawn(obj,e,d,pos,CreationVector.From(matrix.Translation-prev.Translation)*(1/Step),born+m.Delay,0,Seed(obj.ParticleEffect!.Seed,sequence,e.Id.GetHashCode()),matrix);
            else
            {
                var path=GeometryPath(obj,e,born,out bool hit);
                if(hit)foreach(var child in d.Collision)Spawn(obj,e,child,path[^1],default,born+(child.Emitter?.Start??0)+(child.Emitter?.Modules?.Delay??0),1,Seed(obj.ParticleEffect!.Seed,sequence,child.Emitter?.Id.GetHashCode()??0),matrix);
            }
        }
        if(m.DistanceInterval>0)
        {
            double until=Math.Min(now,Math.Min(stop,start+e.Duration));
            var current=EmitterMatrix(obj,e,until).Translation;
            double segment=Vector3.Distance(state.Position,current),distance=state.Distance+segment;
            long first=(long)Math.Floor(state.Distance/m.DistanceInterval)+1,last=(long)Math.Floor((distance+1e-8)/m.DistanceInterval);
            for(long n=first;n<=last;n++)
                Emit(state.Time+(until-state.Time)*Math.Clamp((n*m.DistanceInterval-state.Distance)/Math.Max(segment,1e-12),0,1),n-1);
            moduleSchedules[(obj.Id,e.Id)]=state with {Distance=distance,Position=current,Time=until};return;
        }
        double next=state.Next;long seq=state.Sequence;
        while(next<=now+1e-8 && next<=stop+1e-8)
        {
            double age=next-start,cycle=m.LoopPeriod>0?Math.Floor((age+1e-8)/m.LoopPeriod):0;
            double cycleStart=start+cycle*m.LoopPeriod,local=next-cycleStart;
            if((e.Burst || local<e.Duration-1e-8) && (e.Burst || next<stop-1e-8))Emit(next,seq);
            seq++;
            if(m.Bursts.Count>0)
            {
                var later=m.Bursts.Where(t=>t>local+1e-8 && (m.LoopPeriod<=0||t<m.LoopPeriod)).Order().ToArray();
                next=later.Length>0?cycleStart+later[0]:m.LoopPeriod>0?cycleStart+m.LoopPeriod+m.Bursts.Min():double.PositiveInfinity;
            }
            else if(e.Burst)next=m.LoopPeriod>0?cycleStart+m.LoopPeriod:double.PositiveInfinity;
            else
            {
                var ctx=new ParticleEvaluation(next,local,0,1,default,obj.ParticleEffect!.Seed,wind,temperature,rain);
                next+=Math.Clamp(m.Value("Interval",e.Interval,ctx),.001,86400);
                if(next>=cycleStart+e.Duration-1e-8 || m.LoopPeriod>0&&next>=cycleStart+m.LoopPeriod-1e-8)
                    next=m.LoopPeriod>0?cycleStart+m.LoopPeriod:double.PositiveInfinity;
            }
            if(seq-state.Sequence>4096)throw new CreationParticleBudgetException(e.Name+": schedule work budget exceeded.");
        }
        moduleSchedules[(obj.Id,e.Id)]=state with {Next=next,Sequence=seq};
    }
    private readonly Dictionary<CreationParticleModules,double[]> meshAreas=new();
    private CreationVector ShapePoint(CreationParticleModules m,ref Rng rng)
    {
        if(m.Shape==ParticleSpawnShape.Point)return default;
        float x=(float)(rng.Next()*2-1),y=(float)(rng.Next()*2-1),z=(float)(rng.Next()*2-1);
        double theta=rng.Next()*Math.PI*2;
        var extent=m.Extent.Float();Vector3 p;
        switch(m.Shape)
        {
            case ParticleSpawnShape.Box:
                p=new(x,y,z);if(m.Surface){int face=(int)(rng.Next()*3);p[face]=rng.Next()<.5?-1:1;}return CreationVector.From(p*extent*.5f);
            case ParticleSpawnShape.Sphere:case ParticleSpawnShape.Hemisphere:
                double sy=rng.Next()*2-1,r=Math.Sqrt(1-sy*sy);p=new((float)(r*Math.Cos(theta)),(float)(m.Shape==ParticleSpawnShape.Hemisphere?Math.Abs(sy):sy),(float)(r*Math.Sin(theta)));
                return CreationVector.From(p*extent*(float)(m.Surface?1:Math.Cbrt(rng.Next())));
            case ParticleSpawnShape.Disc:case ParticleSpawnShape.Cylinder:case ParticleSpawnShape.Cone:
                double radius=m.Surface?1:Math.Sqrt(rng.Next());double height=m.Shape==ParticleSpawnShape.Disc?0:rng.Next();
                if(m.Shape==ParticleSpawnShape.Cone)radius*=height;
                return new(Math.Cos(theta)*radius*extent.X,height*extent.Y,Math.Sin(theta)*radius*extent.Z);
            case ParticleSpawnShape.Path:
                if(m.Path.Count<2)return default;double along=rng.Next()*(m.Path.Count-1);int segment=Math.Min(m.Path.Count-2,(int)along);return m.Path[segment]*(1-(along-segment))+m.Path[segment+1]*(along-segment);
            case ParticleSpawnShape.Mesh:
                if(m.Mesh.Count<3)throw new InvalidDataException("Capture a non-empty mesh before using mesh emission.");
                if(!meshAreas.TryGetValue(m,out var areas))
                {
                    areas=new double[m.Mesh.Count/3];double total=0;
                    for(int i=0;i<m.Mesh.Count;i+=3){total+=Vector3.Cross((m.Mesh[i+1]-m.Mesh[i]).Float(),(m.Mesh[i+2]-m.Mesh[i]).Float()).Length();areas[i/3]=total;}
                    if(total<=1e-12)throw new InvalidDataException("Mesh emission requires non-degenerate triangles.");meshAreas[m]=areas;
                }
                int selected=Array.BinarySearch(areas,rng.Next()*areas[^1]);if(selected<0)selected=~selected;
                int tri=Math.Min(areas.Length-1,selected)*3;
                double u=Math.Sqrt(rng.Next()),v=rng.Next();return m.Mesh[tri]*(1-u)+m.Mesh[tri+1]*(u*(1-v))+m.Mesh[tri+2]*(u*v);
            default:return default;
        }
    }
    private ParticleEvaluation Context(Live p,double now)=>new(now,now-p.Object.ParticleStart-p.Emitter.Start,Math.Clamp((now-p.Born)/p.Life,0,1),p.Life,p.Velocity,p.Seed%1000000,wind,temperature,rain);
    private double Value(Live p,string key,double fallback,double time)
        =>p.Emitter.Modules?.Drivers.TryGetValue(key,out var driver)==true
            ?driver.Evaluate(driver.Clock==ParticleClock.Emission?p.BirthContext:Context(p,time)):fallback;
    private bool ModuleDeath(Live p,CreationParticleCell cell)
    {
        if(p.Emitter.Modules is not {} m)return false;
        if(m.KillPlane&&p.Position.Y<m.KillHeight)return true;
        if(m.ExpireBlocks.Length==0)return false;
        bool match=m.ExpireBlocks.Split(',',StringSplitOptions.TrimEntries|StringSplitOptions.RemoveEmptyEntries).Contains(cell.BlockCode,StringComparer.OrdinalIgnoreCase);
        return m.ExpireOutsideBlocks?!match:match;
    }
    private void AdvanceModules(Live p,double now,double dt)
    {
        if(p.Emitter.Modules is not {} m)return;
        var v=p.Velocity;var direction=m.ForceCenter-p.Position;double length=direction.Float().Length();
        var acceleration=m.Acceleration;
        if(length>1e-6)acceleration+=direction*(m.Attraction/length)+new CreationVector(-direction.Z,0,direction.X)*(m.Vortex/length);
        if(m.Noise!=0){var rng=new Rng(Seed((int)p.Seed,tick,731));acceleration+=new CreationVector(rng.Next()*2-1,rng.Next()*2-1,rng.Next()*2-1)*m.Noise;}
        v=(v+acceleration*dt)*Math.Exp(-m.Drag*dt);var ctx=Context(p,now);
        double Dynamic(string key,double fallback)=>m.Drivers.TryGetValue(key,out var driver)&&driver.Clock!=ParticleClock.Emission?driver.Evaluate(ctx):fallback;
        p.Velocity=new(Dynamic("Velocity.X",v.X),Dynamic("Velocity.Y",v.Y),Dynamic("Velocity.Z",v.Z));
    }
    public static Vector4 ParticleUv(CreationParticleModules? m,double age,double life)
    {
        if(m is null)return new(0,0,1,1);
        int frame=(int)Math.Max(0,Math.Floor(m.AnimateByLife?age/Math.Max(.001,life)*m.Frames:age*m.FramesPerSecond));
        frame=m.TextureLoop?frame%m.Frames:Math.Min(m.Frames-1,frame);
        float w=(float)m.UvSize.X/m.Columns,h=(float)m.UvSize.Y/m.Rows;
        float x=(float)m.UvOrigin.X+frame%m.Columns*w,y=(float)m.UvOrigin.Y+frame/m.Columns*h;
        return new(x,y,x+w,y+h);
    }
    private List<CreationVector> GeometryPath(CreationSceneObject obj,CreationParticleEmitter e,double time,out bool hit)
    {
        var m=e.Modules!;var matrix=EmitterMatrix(obj,e,time);
        var origin=CreationVector.From(Vector3.Transform(e.Offset.Float(),matrix));
        var end=m.EndObject!=Guid.Empty?CreationVector.From((Attachment?.Invoke(m.EndObject,m.EndBone,time)??transform(m.EndObject,time)).Translation):CreationVector.From(Vector3.Transform(m.End.Float(),matrix));
        var points=new List<CreationVector>{origin};var rng=new Rng(Seed(obj.ParticleEffect!.Seed,(long)(time*Math.Max(0,m.FlickerRate)),e.Id.GetHashCode()));hit=false;int queries=0;
        for(int i=1;i<=m.Segments;i++)
        {
            double t=(double)i/m.Segments;var p=origin*(1-t)+end*t+new CreationVector(0,m.Bend*Math.Sin(t*Math.PI),0);
            if(m.Geometry==ParticleGeometry.Lightning&&i<m.Segments)p+=new CreationVector(rng.Next()*2-1,rng.Next()*2-1,rng.Next()*2-1)*m.Jitter;
            if(m.ClipAtTerrain)
            {
                var previous=points[^1];int steps=Math.Max(1,(int)Math.Ceiling((p-previous).Float().Length()/.05));
                if(queries+steps>16384)throw new CreationParticleBudgetException(e.Name+": beam terrain query budget exceeded.");queries+=steps;
                for(int step=1;step<=steps;step++){var at=previous+(p-previous)*((double)step/steps);if(Collides(at,.005)){points.Add(at);hit=true;return points;}}
            }
            points.Add(p);
        }
        return points;
    }
    private void BuildRibbons(double time)
    {
        var result=new List<CreationParticleRibbon>();var counts=Particles.Where(p=>p.Mesh is not null).GroupBy(p=>p.ObjectId).ToDictionary(g=>g.Key,g=>g.Sum(p=>p.Mesh!.Count));int total=counts.Values.Sum();
        foreach(var (obj,e,_) in roots)
        {
            if(e.Modules is not {} m||m.Geometry==ParticleGeometry.Particle)continue;
            double age=time-obj.ParticleStart-e.Start;if(age<0||age>Math.Min(e.Duration,obj.ParticleDuration-e.Start)+(m.Geometry==ParticleGeometry.Trail?m.Retain:0))continue;
            var matrix=EmitterMatrix(obj,e,time);var origin=CreationVector.From(Vector3.Transform(e.Offset.Float(),matrix));
            var end=m.EndObject!=Guid.Empty?CreationVector.From((Attachment?.Invoke(m.EndObject,m.EndBone,time)??transform(m.EndObject,time)).Translation):CreationVector.From(Vector3.Transform(m.End.Float(),matrix));
            var points=new List<CreationVector>();var rng=new Rng(Seed(obj.ParticleEffect!.Seed,(long)(time*Math.Max(0,m.FlickerRate)),e.Id.GetHashCode()));
            if(m.Geometry==ParticleGeometry.Trail && m.TrailEmitter==Guid.Empty)
            {
                double stop=Math.Min(time,obj.ParticleStart+e.Start+e.Duration),begin=Math.Max(obj.ParticleStart+e.Start,time-m.Retain);
                int samples=0;for(double t=Math.Ceiling(begin/Step)*Step;t<=stop+1e-8;t+=Step)
                {if(++samples>EffectVertexBudget)throw new CreationParticleBudgetException(e.Name+": trail sampling budget exceeded.");var p=CreationVector.From(Vector3.Transform(e.Offset.Float(),EmitterMatrix(obj,e,t)));if(points.Count==0||Vector3.Distance(points[^1].Float(),p.Float())>=m.SampleDistance)points.Add(p);if(points.Count>EffectVertexBudget/2)throw new CreationParticleBudgetException(e.Name+": trail vertex budget exceeded.");}
            }
            else if(m.Geometry!=ParticleGeometry.Trail)points.AddRange(GeometryPath(obj,e,time,out _));
            void Add(CreationVector[] path)
            {
                if(path.Length<2)return;int vertices=path.Length*2;total+=vertices;counts[obj.Id]=counts.GetValueOrDefault(obj.Id)+vertices;
                if(total>ProjectVertexBudget||counts[obj.Id]>EffectVertexBudget)throw new CreationParticleBudgetException(obj.Name+" / "+e.Name+": geometry budget exceeded.");
                var ctx=new ParticleEvaluation(time,age,age/Math.Max(.001,e.Duration),e.Duration,default,obj.ParticleEffect.Seed,wind);
                var color=m.Gradient.Enabled?m.Gradient.Sample(Math.Clamp(ctx.Age,0,1)):(CreationVector.One,1d);
                var widths=new double[path.Length];var colors=new CreationVector[path.Length];var alphas=new double[path.Length];
                for(int i=0;i<path.Length;i++)
                {
                    double along=(double)i/(path.Length-1);widths[i]=Math.Max(0,m.RibbonProfile.Sample(along));
                    var pointColor=m.Gradient.Enabled?m.Gradient.Sample(along):color;colors[i]=pointColor.Item1;
                    alphas[i]=pointColor.Item2*(m.FadeEnds?Math.Min(1,Math.Min(along,1-along)*10):1);
                    if(m.Geometry==ParticleGeometry.Trail)alphas[i]*=along;
                }
                result.Add(new(obj.Id,e.Id,path,Math.Max(0,m.Value("RibbonWidth",m.RibbonWidth,ctx)),color.Item1,color.Item2,e.Texture,e.Additive,m.Value("Glow",1,ctx),time*m.TextureFlow,widths,colors,alphas));
            }
            if(m.Geometry==ParticleGeometry.Trail&&m.TrailEmitter!=Guid.Empty)foreach(var track in particleTrails.Values.Where(t=>t.Object==obj.Id&&t.Trail==e.Id))Add(track.Points.Select(p=>p.Position).ToArray());
            else Add(points.ToArray());
            if(m.Geometry==ParticleGeometry.Lightning&&points.Count>2)for(int branch=0;branch<m.Branches;branch++)
            {int index=1+(int)(rng.Next()*(points.Count-2));var from=points[index];Add([from,from+new CreationVector(rng.Next()-.5,rng.Next()-.5,rng.Next()-.5)*m.Jitter*4,from+new CreationVector(rng.Next()-.5,-rng.Next(),rng.Next()-.5)*m.Jitter*8]);}
        }
        Ribbons=result;
    }
}
