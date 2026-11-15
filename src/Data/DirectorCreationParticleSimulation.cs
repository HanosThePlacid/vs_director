using System.Numerics;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace VSDirector.Data;

public readonly record struct CreationParticleBox(CreationVector Min, CreationVector Max);
public readonly record struct CreationParticleCell(bool Solid, bool Liquid, double RainHeight, CreationVector Color, CreationParticleBox[]? Boxes = null, string BlockCode = "");
public interface ICreationParticleEnvironment
{
    CreationVector Wind { get; }
    // World voxel coordinates = project coordinates + GridOrigin.
    CreationVector GridOrigin => default;
    double Temperature => 20;
    double Rain => 0;
    CreationVector BlockColor(string code) => CreationVector.One;
    CreationParticleCell Cell(int x, int y, int z);
}
public sealed class CreationEmptyParticleEnvironment : ICreationParticleEnvironment
{
    public CreationVector Wind => default;
    public CreationParticleCell Cell(int x,int y,int z) => new(false,false,double.NegativeInfinity,CreationVector.One);
}
public sealed record CreationSimulatedParticle(Guid ObjectId, Guid EmitterId, long Index, CreationVector Position,
    double Size, double Alpha, CreationVector Color, double Glow, bool Cube, bool Soft, bool Additive, string Texture, double Rotation = 0,
    CreationVector ShapeScale = default, Quaternion ShapeRotation = default, double LegacyProgress = -1,
    Vector4 Uv = default, ParticleFacing Facing = ParticleFacing.Camera, CreationVector Velocity = default, IReadOnlyList<CreationVector>? Mesh = null);
public sealed class CreationParticleBudgetException(string message) : InvalidOperationException(message);

/// <summary>Fixed 120 Hz simulation. A render never advances it; all views consume the same completed frame.</summary>
public sealed partial class DirectorCreationParticleSimulation
{
    public const double Step = 1d/120;
    public const int EffectBudget = 6000, ProjectBudget = 24000, MaxDepth = 8;
    private readonly DirectorCreationProject project;
    private readonly Func<Guid,double,Matrix4x4> transform;
    private readonly ICreationParticleEnvironment environment;
    // Lock each queried input for the lifetime of this rebuild, including backwards seeks.
    private readonly Dictionary<(int,int,int),CreationParticleCell> cells = new();
    private readonly CreationVector wind;
    private readonly double temperature,rain;
    private readonly List<(CreationSceneObject Object,CreationParticleEmitter Emitter,Definition Definition)> roots = new();
    private List<Live> live = new();
    private readonly SortedDictionary<long,List<Live>> snapshots = new();
    private long tick = -1, nextId;
    private readonly Dictionary<long,long> snapshotIds = new();
    private readonly Dictionary<JsonObject,int> samples=new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<long,Dictionary<JsonObject,int>> snapshotSamples=new();
    private long target;
    private int cellQueries;
    private bool cancelled;
    public double Time => Math.Max(0,tick)*Step;
    public double Progress => target <= 0 ? 1 : Math.Clamp((double)Math.Max(0,tick)/target,0,1);
    public bool Ready => tick == target && !cancelled && Error.Length==0;
    public string Error { get; private set; } = "";
    public IReadOnlyList<CreationSimulatedParticle> Particles { get; private set; } = [];
    public DirectorCreationParticleSimulation(DirectorCreationProject project, Func<Guid,double,Matrix4x4> transform, ICreationParticleEnvironment? environment = null)
    {
        this.project=project;this.transform=transform;this.environment=environment??new CreationEmptyParticleEnvironment();wind=this.environment.Wind;temperature=this.environment.Temperature;rain=this.environment.Rain;
        foreach(var obj in project.Objects.Where(o=>o.Kind==CreationObjectKind.Particle && o.ParticleEffect is not null))
        {
            obj.ParticleEffect!.Validate();
            foreach(var e in obj.ParticleEffect.Emitters.Where(e=>e.Enabled)) roots.Add((obj,e,new(e.Properties,samples,e)));
        }
    }
    public void Cancel() { cancelled=true;Particles=[];Ribbons=[]; }
    public bool Seek(double time, int maxSteps = 600)
    {
        if (!double.IsFinite(time) || time<0 || time>86400) throw new ArgumentOutOfRangeException(nameof(time));
        target=(long)Math.Floor(time/Step+1e-7);
        if(cancelled || Error.Length>0)return false;
        if(target<tick)
        {
            long saved=snapshots.Keys.Where(k=>k<=target).DefaultIfEmpty(-1).Max();
            live=saved<0?new():snapshots[saved].Select(p=>p.Copy()).ToList();tick=saved;nextId=saved<0?0:snapshotIds[saved];
            particleTrails=saved>=0&&trailSnapshots.TryGetValue(saved,out var trails)?CopyTrails(trails):new();
            moduleSchedules.Clear();if(saved>=0&&moduleSnapshots.TryGetValue(saved,out var schedule))foreach(var pair in schedule)moduleSchedules[pair.Key]=pair.Value;
            samples.Clear();if(saved>=0)foreach(var pair in snapshotSamples[saved])samples.Add(pair.Key,pair.Value);
        }
        try
        {
            var work=Stopwatch.StartNew();
            for(int i=0;tick<target && i<maxSteps && (i==0||work.ElapsedMilliseconds<8);i++)
            {
                tick++;cellQueries=0; double now=tick*Step;
                if(tick>0)Advance(now);
                foreach(var (obj,e,d) in roots)
                {
                    if(e.Legacy is not null)continue;
                    if(e.Modules is not null){EmitModuleRoot(obj,e,d,now);continue;}
                    if(e.Start>obj.ParticleDuration)continue;
                    double start=obj.ParticleStart+e.Start;
                    long first=Math.Max(0,(long)Math.Ceiling(((tick==0?0:(tick-1)*Step)-start)/e.Interval-1e-8));
                    long last=(long)Math.Floor((now-start)/e.Interval+1e-8);
                    for(long emission=first;emission<=last;emission++)
                    {
                        double born=start+emission*e.Interval;
                        if(born<0 || tick>0 && born<=(tick-1)*Step+1e-8 || e.Burst&&emission>0 || !e.Burst&&born>=start+Math.Max(0,Math.Min(e.Duration,obj.ParticleDuration-e.Start))-1e-8)continue;
                        if(e.Recipe==CreationParticleRecipe.ReferenceFire)
                        {var selection=new Rng(Seed(obj.ParticleEffect!.Seed,emission,918));int choice=Math.Min(e.ProviderCount-1,(int)(selection.Next()*(e.ProviderCount+1)));if(e.ProviderIndex!=choice)continue;}
                        var matrix=transform(obj.Id,born);
                        var pos=CreationVector.From(Vector3.Transform(e.Offset.Float(),matrix));
                        var previous=transform(obj.Id,Math.Max(0,born-Step));
                        var parentVelocity=CreationVector.From(matrix.Translation-previous.Translation)*(1/Step);
                        Spawn(obj,e,d,pos,parentVelocity,born,0,Seed(obj.ParticleEffect!.Seed,emission,e.Id.GetHashCode()),matrix);
                    }
                }
                RecordTrails(now);
                if(tick%120==0)
                {
                    trailSnapshots[tick]=CopyTrails(particleTrails);
                    moduleSnapshots[tick]=new(moduleSchedules);
                    snapshots[tick]=live.Select(p=>p.Copy()).ToList();snapshotIds[tick]=nextId;snapshotSamples[tick]=new(samples,ReferenceEqualityComparer.Instance);
                    while(snapshots.Count>24) { long oldest=snapshots.Keys.First(); snapshots.Remove(oldest);snapshotIds.Remove(oldest);snapshotSamples.Remove(oldest);moduleSnapshots.Remove(oldest);trailSnapshots.Remove(oldest); }
                }
            }
            if(tick==target){cellQueries=0;BuildOutput(time);}
            return Ready;
        }
        catch(Exception e) when(e is CreationParticleBudgetException or InvalidDataException or ArithmeticException){ Error=e.Message;Particles=[];Ribbons=[];throw; }
    }
    private CreationParticleCell Cell(CreationVector pos)
    {
        if(++cellQueries>250000)throw new CreationParticleBudgetException("Particle environment query budget exceeded for one simulation step.");
        var grid=pos+environment.GridOrigin;
        var key=((int)Math.Floor(grid.X),(int)Math.Floor(grid.Y),(int)Math.Floor(grid.Z));
        if(!cells.TryGetValue(key,out var c))cells[key]=c=environment.Cell(key.Item1,key.Item2,key.Item3);
        return c;
    }
    private bool Collides(CreationVector position,double radius)
    {
        var grid=position+environment.GridOrigin;
        int minX=(int)Math.Floor(grid.X-radius),maxX=(int)Math.Floor(grid.X+radius);
        int minY=(int)Math.Floor(grid.Y-radius),maxY=(int)Math.Floor(grid.Y+radius);
        int minZ=(int)Math.Floor(grid.Z-radius),maxZ=(int)Math.Floor(grid.Z+radius);
        for(int x=minX;x<=maxX;x++)for(int y=minY;y<=maxY;y++)for(int z=minZ;z<=maxZ;z++)
        {
            var cell=Cell(new CreationVector(x+.5,y+.5,z+.5)-environment.GridOrigin);if(!cell.Solid)continue;
            var boxes=cell.Boxes??[new(new(0,0,0),new(1,1,1))];
            foreach(var box in boxes)
                if(grid.X+radius>x+box.Min.X && grid.X-radius<x+box.Max.X && grid.Y+radius>y+box.Min.Y && grid.Y-radius<y+box.Max.Y && grid.Z+radius>z+box.Min.Z && grid.Z-radius<z+box.Max.Z)return true;
        }
        return false;
    }
    private void Spawn(CreationSceneObject obj,CreationParticleEmitter e,Definition d,CreationVector origin,CreationVector inherited,double born,int depth,ulong seed,Matrix4x4 matrix)
    {
        if(depth>MaxDepth)throw new CreationParticleBudgetException($"{obj.Name}: child particle depth exceeds {MaxDepth}.");
        e=d.Emitter??e;
        var rng=new Rng(seed);var context=new ParticleEvaluation(born,born-obj.ParticleStart-e.Start,0,1,inherited,seed%1000000,wind,temperature,rain);
        if(e.Modules is {Probability:<1} chance && rng.Next()>chance.Probability)return;
        double quantity=Math.Max(0,e.Modules?.Value("Quantity",d.Sample("Quantity",ref rng,1),context)??d.Sample("Quantity",ref rng,1));
        quantity*=obj.ParticleEffect!.GameSpeed*obj.ParticleEffect.Density;
        int count=(int)Math.Min(100000,Math.Floor(quantity)+(rng.Next()<quantity-Math.Floor(quantity)?1:0));
        if(live.Count+count>ProjectBudget || live.Count(p=>p.Object.Id==obj.Id)+count>EffectBudget)
            throw new CreationParticleBudgetException($"{obj.Name}: particle budget exceeded ({EffectBudget} per effect / {ProjectBudget} per project).");
        var batchOffset=e.Recipe==CreationParticleRecipe.ReferenceFire&&e.Modules is {} recipe?ShapePoint(recipe,ref rng):default;
        for(int i=0;i<count;i++)
        {
            var prng=new Rng(Seed((int)seed,i,depth));
            var pos=d.Vector("PosOffset",ref prng);
            var velocity=d.Vector("Velocity",ref prng);
            if(e.Modules is {} module){var offset=e.Recipe==CreationParticleRecipe.ReferenceFire?batchOffset:ShapePoint(module,ref prng);pos+=offset;
                if(module.Direction!=ParticleDirection.Velocity && offset.Float().LengthSquared()>1e-12)velocity=CreationVector.From(Vector3.Normalize(offset.Float()))*(module.Direction==ParticleDirection.Inwards?-module.DirectionSpeed:module.DirectionSpeed);
                velocity=new(module.Value("Velocity.X",velocity.X,context),module.Value("Velocity.Y",velocity.Y,context),module.Value("Velocity.Z",velocity.Z,context));}
            if(depth==0){pos=CreationVector.From(Vector3.TransformNormal(pos.Float(),matrix));velocity=CreationVector.From(Vector3.TransformNormal(velocity.Float(),matrix));}
            pos+=origin;
            var hsva=d.Vector4("HsvaColor",ref prng);
            var color=Hsv(hsva.X,hsva.Y,hsva.Z);
            if(d.Flag("ColorByBlock"))color=Cell(pos).Color;
            if(e.Modules is {ColorBlock.Length:>0} colorModule)color=environment.BlockColor(colorModule.ColorBlock);
            double initialLife=d.Sample("LifeLength",ref prng,1);
            double life=Math.Max(.001,(e.Modules?.Value("LifeLength",initialLife,context)??initialLife)*5/Math.Pow(obj.ParticleEffect.GameSpeed,1.5));
            double[] nextChildren=d.Secondary.Select(child=>born+Math.Max(Step,child.Sample("SecondarySpawnInterval",ref prng))).ToArray();
            live.Add(new(){Id=nextId++,Object=obj,Emitter=e,Def=d,Position=pos,Velocity=velocity,StartingVelocity=velocity,
                Inherited=inherited*(depth>0?(e.Modules?.InheritVelocity??e.ParentVelocityWeight):e.ParentVelocityWeight), Origin=pos, Born=born,Life=life,Size=d.Sample("Size",ref prng,1),
                Gravity=d.Sample("GravityEffect",ref prng,1)*12,Color=color,Alpha=hsva.W/255,Depth=depth,Seed=prng.State,NextChildren=nextChildren,
                Matrix=matrix,BirthContext=context with {Seed=prng.State%1000000,Life=life,Velocity=velocity}});
            foreach(var child in d.Birth)Spawn(obj,e,child,pos,velocity,born+(child.Emitter?.Start??0)+(child.Emitter?.Modules?.Delay??0),depth+1,Seed((int)seed,i,child.Emitter?.Id.GetHashCode()??0),matrix);
        }
    }
    private void Advance(double now)
    {
        var children=new List<(Live Parent,Definition Def,bool Death,int Child)>();
        for(int i=live.Count-1;i>=0;i--)
        {
            var p=live[i];if(now<p.Born)continue;double dt=Math.Min(Step,Math.Max(0,now-p.Born));
            dt*=p.Object.ParticleEffect!.GameSpeed;
            var d=p.Def;var old=p.Position;var cell=Cell(old);
            bool death=now-p.Born>=p.Life || d.Flag("DieInAir")&&!cell.Liquid || d.Flag("DieInLiquid")&&cell.Liquid
                || d.Flag("DieOnRainHeightmap")&&old.Y<cell.RainHeight || ModuleDeath(p,cell);
            if(!death)
            {
                if(d.Flag("SelfPropelled")&&d.Flag("TerrainCollision",true))p.Velocity+=(p.StartingVelocity-p.Velocity)*(1-Math.Pow(.98,dt*30));
                p.Velocity+=new CreationVector(0,-Value(p,"GravityEffect",p.Gravity/12,now)*12*dt,0);
                AdvanceModules(p,now,dt);
                double f=Math.Clamp((now-p.Born)/p.Life,0,1);
                var velocity=p.Velocity;
                if(d.VelocityEvolve is { } ve)velocity=new(velocity.X*Evolve(ve[0],0,f),velocity.Y*Evolve(ve[1],0,f),velocity.Z*Evolve(ve[2],0,f));
                velocity+=p.Inherited+wind*d.Number("WindAffectednes");
                var desired=old+velocity*dt;
                if(p.Emitter.Modules is {Parametric:true} param){var ctx=Context(p,now);desired=p.Origin+new CreationVector(Value(p,"Position.X",0,now),Value(p,"Position.Y",0,now),Value(p,"Position.Z",0,now));}
                bool collided=false;
                if(d.Flag("TerrainCollision",true))
                {
                    // Swept, axis-separated substeps avoid tunnelling through thin collision boxes.
                    double radius=Math.Clamp(Math.Abs(EvolveValue(d.Node("SizeEvolve"),p.Size,f))*.125,.001,2);
                    int substeps=Math.Clamp((int)Math.Ceiling(Math.Max(Math.Abs(desired.X-old.X),Math.Max(Math.Abs(desired.Y-old.Y),Math.Abs(desired.Z-old.Z)))/.05),1,256);
                    var delta=(desired-old)*(1d/substeps);desired=old;
                    for(int sub=0;sub<substeps;sub++)
                    {
                        var test=desired with{X=desired.X+delta.X};
                        if(Collides(test,radius)){collided=true;delta=delta with{X=0};p.Velocity=p.Velocity with{X=-p.Velocity.X*d.Number("Bounciness")*.65};}else desired=test;
                        test=desired with{Y=desired.Y+delta.Y};
                        if(Collides(test,radius)){collided=true;delta=delta with{Y=0};p.Velocity=p.Velocity with{Y=-p.Velocity.Y*d.Number("Bounciness")*.65};}else desired=test;
                        test=desired with{Z=desired.Z+delta.Z};
                        if(Collides(test,radius)){collided=true;delta=delta with{Z=0};p.Velocity=p.Velocity with{Z=-p.Velocity.Z*d.Number("Bounciness")*.65};}else desired=test;
                    }
                }
                if(d.Flag("SwimOnLiquid")&&Cell(desired).Liquid&&velocity.Y<0){desired=desired with{Y=Math.Floor(desired.Y+environment.GridOrigin.Y)+1-environment.GridOrigin.Y};p.Velocity=p.Velocity with{Y=0};}
                if(!desired.Finite()||!p.Velocity.Finite())throw new InvalidDataException(p.Emitter.Name+": non-finite particle motion.");
                p.Position=desired;
                if(collided){p.Velocity*=Math.Max(0,1-(p.Emitter.Modules?.Friction??0));foreach(var child in d.Collision)children.Add((p,child,false,1000));}
                for(int j=0;j<d.Secondary.Length;j++)if(now+1e-8>=p.NextChildren[j])
                {
                    children.Add((p,d.Secondary[j],false,j));
                    var random=new Rng(Seed((int)p.Seed,tick,j));
                    p.NextChildren[j]=now+Math.Max(Step,d.Secondary[j].Sample("SecondarySpawnInterval",ref random));
                }
                if(d.Flag("RandomVelocityChange"))
                {
                    var random=new Rng(p.RandomState==0?p.Seed:p.RandomState);
                    if(p.Wander>0)
                    {
                        double ax=Math.Sin(Math.PI*2*p.Wander),az=Math.Cos(7.539823*p.Wander);
                        p.Velocity+=new CreationVector(p.Direction*ax*4,(p.Direction*ax*10-p.Direction*az*3)/10,az*3)*(dt*p.Size);
                        p.Wander+=dt/3;if(p.Wander>2||random.Next()<1-Math.Pow(.995,dt*30))p.Wander=0;
                    }
                    else p.Velocity+=new CreationVector((p.StartingVelocity.X-p.Velocity.X)*dt,0,(p.StartingVelocity.Z-p.Velocity.Z)*dt);
                    if(random.Next()<1-Math.Pow(.995,dt*30)){p.Wander=random.Next()*.5;p.Direction=random.Next()<.5?-1:1;}
                    p.RandomState=random.State;
                }
            }
            else
            {
                live.RemoveAt(i);for(int j=0;j<d.Death.Length;j++)children.Add((p,d.Death[j],true,j));
            }
        }
        foreach(var c in children)
        {var p=c.Parent;Spawn(p.Object,p.Emitter,c.Def,p.Position,p.Velocity,now+(c.Def.Emitter?.Start??0)+(c.Def.Emitter?.Modules?.Delay??0),p.Depth+1,Seed((int)p.Seed,tick,c.Child+(c.Death?10000:0)),p.Matrix);}
    }
    private void BuildOutput(double time)
    {
        var output=new List<CreationSimulatedParticle>();
        foreach(var p in live)
        {
            if(Time<p.Born)continue;
            double f=Math.Clamp((Time-p.Born)/p.Life,0,1);var d=p.Def;
            double size=Math.Max(0,EvolveValue(d.Node("SizeEvolve"),p.Size,f))*.25;
            double alpha=Math.Clamp(EvolveValue(d.Node("OpacityEvolve"),p.Alpha*255,f)/255,0,1);
            var color=new CreationVector(Channel("RedEvolve",p.Color.X),Channel("GreenEvolve",p.Color.Y),Channel("BlueEvolve",p.Color.Z));
            double Channel(string key,double value)=>HasEvolution(d.Node(key))?Math.Clamp(value+Evolve(d.Node(key),value*255,f)/255,0,1):value;
            var pos=p.Position;
            if(p.Emitter.LocalFollow && Matrix4x4.Invert(p.Matrix,out var inverse))pos=CreationVector.From(Vector3.Transform(Vector3.Transform(pos.Float(),inverse),EmitterMatrix(p.Object,p.Emitter,Time)));
            var ctx=Context(p,Time);var m=p.Emitter.Modules;
            if(m is not null){size=Math.Max(0,Value(p,"Size",size*4,Time))*.25;alpha=Math.Clamp(Value(p,"Alpha",alpha,Time),0,1);
                if(m.Gradient.Enabled){var g=m.Gradient.Sample(f);color=g.Color;alpha*=g.Alpha;}
                color=new(Value(p,"Color.R",color.X,Time),Value(p,"Color.G",color.Y,Time),Value(p,"Color.B",color.Z,Time));}
            if(m is {MeshParticle:true,Mesh.Count:0})throw new InvalidDataException(p.Emitter.Name+": capture a mesh before rendering mesh particles.");
            output.Add(new(p.Object.Id,p.Emitter.Id,p.Id,pos,size,alpha,color,Value(p,"Glow",(d.Number("VertexFlags")%256)/255,Time),d.Model=="Cube",p.Emitter.SoftEdge,p.Emitter.Additive,p.Emitter.Texture,
                m is null?0:m.InitialRotation+Value(p,"Spin",m.Spin,Time)*(Time-p.Born)+.5*m.SpinAcceleration*Math.Pow(time-p.Born,2),
                m is null?default:new(Value(p,"Width",m.Width,Time),Value(p,"Height",m.Height,Time),1),default,-1,ParticleUv(m,Time-p.Born,p.Life),m?.Facing??ParticleFacing.Camera,p.Velocity,m is {MeshParticle:true}?m.Mesh:null));
        }
        foreach(var (obj,e,_) in roots.Where(r=>r.Emitter.Legacy is not null))
        {
            var legacy=e.Legacy!;var s=legacy.ParticleSettings??CreationParticleSettings.Preset(legacy.ResourceCode);
            var scheduled=CreationJson.Clone(legacy);
            scheduled.ParticleDuration=Math.Max(0,Math.Min(e.Duration,obj.ParticleDuration-e.Start));
            scheduled.ParticleSettings=CreationJson.Clone(s);scheduled.ParticleSettings.Burst=e.Burst;
            if(!e.Burst)scheduled.ParticleCount=(int)Math.Clamp(Math.Round(1/e.Interval),1,10000);
            double local=time-obj.ParticleStart-e.Start+legacy.ParticleStart;
            foreach(var p in DirectorCreationParticles.Sample(scheduled,local))
            {
                double born=time-p.Age;var matrix=transform(obj.Id,e.LocalFollow?time:born);
                Matrix4x4.Decompose(matrix,out var scale,out var rotation,out _);
                output.Add(new(obj.Id,e.Id,p.Index,CreationVector.From(Vector3.Transform((p.Position+e.Offset).Float(),matrix)),p.Size,p.Alpha,
                    legacy.Color*(1-p.Progress)+s.EndColor*p.Progress,legacy.Glow,s.Shape==CreationParticleShape.Cube,s.Shape==CreationParticleShape.SoftQuad,e.Additive,e.Texture,p.Rotation,
                    CreationVector.From(Vector3.Abs(scale)),rotation,p.Progress));
            }
        }
        foreach(var group in output.GroupBy(p=>p.ObjectId))if(group.Count()>EffectBudget)throw new CreationParticleBudgetException($"{project.Objects.First(o=>o.Id==group.Key).Name}: particle budget exceeded.");
        if(output.Count>ProjectBudget)throw new CreationParticleBudgetException("Project particle budget exceeded.");
        var meshCounts=output.Where(p=>p.Mesh is not null).GroupBy(p=>p.ObjectId);
        int meshTotal=0;foreach(var group in meshCounts){int vertices=group.Sum(p=>p.Mesh!.Count);meshTotal+=vertices;if(vertices>EffectVertexBudget||meshTotal>ProjectVertexBudget)throw new CreationParticleBudgetException(project.Objects.First(o=>o.Id==group.Key).Name+": mesh vertex budget exceeded.");}
        Particles=output;BuildRibbons(Time);
    }
    public static double Evolve(JsonNode? node,double first,double seq)
    {
        if(node is not JsonObject o)return first;
        double k=CreationParticleFields.Number(o,"factor"), x=k*seq;
        string name=CreationParticleFields.FunctionName(CreationParticleFields.Find(o,"transform"),false).ToUpperInvariant();
        if(name=="UNSPECIFIED")return 0;
        double v=name switch {
            "LINEAR"=>first+x,"INVERSELINEAR"=>first+1/(1+x),"LINEARNULLIFY"=>k>0?Math.Min(0,first+x):Math.Max(0,first+x),
            "LINEARREDUCE"=>first-Math.Sign(first)*x,"LINEARINCREASE"=>first+Math.Sign(first)*x,
            "QUADRATIC"=>first+Math.Sign(k)*x*x,"ROOT"=>first+Math.Sqrt(x),"SINUS"=>first+Wave(x,false),
            "CLAMPEDPOSITIVESINUS"=>first*Math.Min(5*Math.Abs(Wave(x,false)),1),"COSINUS"=>first+Wave(x,true),
            "SMOOTHSTEP"=>first+x*x*(3-2*x),_=>first};
        if(CreationParticleFields.Find(o,"maxvalue") is not null)v=Math.Min(v,CreationParticleFields.Number(o,"maxvalue"));return double.IsFinite(v)?v:0;
    }
    private static bool HasEvolution(JsonNode? node)=>node is JsonObject o&&CreationParticleFields.FunctionName(CreationParticleFields.Find(o,"transform"),false)!="UNSPECIFIED";
    private static double EvolveValue(JsonNode? node,double first,double seq)=>HasEvolution(node)?Evolve(node,first,seq):first;
    // Match the engine's 4096-bin, midpoint-sampled trigonometric lookup. Cardinal
    // directions use the exact angle; ordinary Math.Sin changes bright/fade pulses.
    private static readonly double[] sine=WaveTable(false),cosine=WaveTable(true);
    private static double[] WaveTable(bool cos)
    {
        float full=(float)(2*Math.PI);
        var table=Enumerable.Range(0,4096).Select(i=>(double)(float)(cos?Math.Cos((i+.5f)/4096*full):Math.Sin((i+.5f)/4096*full))).ToArray();
        for(int degrees=0;degrees<360;degrees+=90)table[(int)(degrees*(4096/360f))&4095]=(float)(cos?Math.Cos(degrees*Math.PI/180):Math.Sin(degrees*Math.PI/180));
        return table;
    }
    private static double Wave(double angle,bool cos)=>(cos?cosine:sine)[(int)((float)angle*(4096/(float)(2*Math.PI)))&4095];
    private static CreationVector Hsv(double h,double s,double v)
    {
        int hue=(int)Math.Clamp(h,0,255),sat=(int)Math.Clamp(s,0,255),value=(int)Math.Clamp(v,0,255);
        if(sat==0||value==0)return new(value/255d,value/255d,value/255d);
        int region=hue/43,remainder=(hue-region*43)*6;
        int p=(value*(255-sat))>>8,q=(value*(255-((sat*remainder)>>8)))>>8,t=(value*(255-((sat*(255-remainder))>>8)))>>8;
        var rgb=region switch{0=>new CreationVector(value,t,p),1=>new(q,value,p),2=>new(p,value,t),3=>new(p,q,value),4=>new(t,p,value),_=>new(value,p,q)};
        return rgb*(1d/255);
    }
    private static ulong Seed(int seed,long index,int channel)
    {unchecked{ulong x=(uint)seed ^ (ulong)index*0x9e3779b97f4a7c15UL ^ (uint)channel*0x85ebca6bUL;x=(x^(x>>30))*0xbf58476d1ce4e5b9UL;x=(x^(x>>27))*0x94d049bb133111ebUL;return x^(x>>31);}}
    private struct Rng(ulong seed)
    { public ulong State=seed;public double Next(){State+=0x9e3779b97f4a7c15UL;ulong z=State;z=(z^(z>>30))*0xbf58476d1ce4e5b9UL;z=(z^(z>>27))*0x94d049bb133111ebUL;return ((z^(z>>31))>>11)*(1d/(1UL<<53));} }
    private sealed class Definition
    {
        private readonly JsonObject properties;
        private readonly Dictionary<JsonObject,int> samples;
        public Definition[] Secondary,Death,Birth,Collision;
        public CreationParticleEmitter? Emitter;
        public JsonArray? VelocityEvolve;
        public string Model;
        public Definition(JsonObject p,Dictionary<JsonObject,int> samples,CreationParticleEmitter? emitter=null){Emitter=emitter;this.samples=samples;properties=(JsonObject)p.DeepClone();
            // Missing native fields use the game defaults; authoring defaults remain intentionally simple.
            if(Node("HsvaColor") is null && !p.Any(k=>k.Key.Equals("HsvaColor",StringComparison.OrdinalIgnoreCase)))properties["HsvaColor"]=new JsonArray(CreationParticleFields.Nat(128,128),CreationParticleFields.Nat(128,128),CreationParticleFields.Nat(128,128),CreationParticleFields.Nat(255));
            if(Node("Velocity") is null)properties["Velocity"]=new JsonArray(CreationParticleFields.Nat(0,.5),CreationParticleFields.Nat(0,.5),CreationParticleFields.Nat(0,.5));Secondary=Children("SecondaryParticles").Concat(Nodes(ParticleTrigger.Periodic)).ToArray();Death=Children("DeathParticles").Concat(Nodes(ParticleTrigger.Death)).ToArray();Birth=Nodes(ParticleTrigger.Birth);Collision=Nodes(ParticleTrigger.Collision);VelocityEvolve=Node("VelocityEvolve") as JsonArray;if(VelocityEvolve is not null&&VelocityEvolve.All(n=>n is null))VelocityEvolve=null;Model=Node("ParticleModel")?.ToString()??"Cube";if(Model=="0")Model="Quad";if(Model=="1")Model="Cube";}
        private Definition[] Nodes(ParticleTrigger trigger)=>Emitter?.Children.Where(c=>c.Enabled&&c.Trigger==trigger).Select(c=>new Definition(c.Properties,samples,c)).ToArray()??[];
        private Definition[] Children(string key)=>(Node(key) as JsonArray)?.OfType<JsonObject>().Select(c=>new Definition(c,samples)).ToArray()??[];
        public JsonNode? Node(string key)=>CreationParticleFields.Find(properties,key);
        public bool Flag(string key,bool fallback=false)=>CreationParticleFields.Flag(properties,key,fallback);
        public double Number(string key)=>CreationParticleFields.Number(properties,key);
        public double Sample(string key,ref Rng rng,double fallback=0)=>SampleNode(Node(key),ref rng,fallback);
        public CreationVector Vector(string key,ref Rng rng){var a=Node(key) as JsonArray;return new(SampleNode(a?[0],ref rng),SampleNode(a?[1],ref rng),SampleNode(a?[2],ref rng));}
        public Vector4 Vector4(string key,ref Rng rng){var a=Node(key) as JsonArray;return new((float)SampleNode(a?[0],ref rng,255),(float)SampleNode(a?[1],ref rng,0),(float)SampleNode(a?[2],ref rng,255),(float)SampleNode(a?[3],ref rng,255));}
        private double SampleNode(JsonNode? node,ref Rng r,double fallback=0)
        {
            if(node is not JsonObject n)return fallback;
            double avg=CreationParticleFields.Number(n,"avg"),v=CreationParticleFields.Number(n,"var"),offset=CreationParticleFields.Number(n,"offset");
            string dist=CreationParticleFields.FunctionName(CreationParticleFields.Find(n,"dist"),true).ToLowerInvariant();
            if(dist=="dirac"){samples.TryGetValue(n,out int used);samples[n]=used+1;if(used>0)return offset;}
            int count=dist switch{"triangle"=>2,"gaussian" or "inversegaussian"=>3,"narrowgaussian" or "narrowinversegaussian"=>6,"verynarrowgaussian"=>12,_=>1};
            double sample=0;
            if(dist is "invexp" or "stronginvexp" or "strongerinvexp")
            {sample=r.Next()*r.Next();if(dist!="invexp")sample*=r.Next();if(dist=="strongerinvexp")sample*=r.Next();}
            else {for(int i=0;i<count;i++)sample+=r.Next();sample/=count;if(dist.Contains("inversegaussian"))sample=sample>.5?sample-.5:sample+.5;sample=(sample-.5)*2;}
            return offset+avg+sample*v;
        }
    }
    private sealed class Live
    {
        public long Id;public CreationSceneObject Object=null!;public CreationParticleEmitter Emitter=null!;public Definition Def=null!;
        public CreationVector Position,Origin,Velocity,StartingVelocity,Inherited,Color;public double Born,Life,Size,Alpha,Gravity,Wander,Direction=1;
        public ParticleEvaluation BirthContext;
        public double[] NextChildren=[];public ulong RandomState;
        public int Depth;public ulong Seed;public Matrix4x4 Matrix;
        public Live Copy(){var copy=(Live)MemberwiseClone();copy.NextChildren=(double[])NextChildren.Clone();return copy;}
    }
}
