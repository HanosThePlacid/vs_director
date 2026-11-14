using System.Numerics;
using System.Diagnostics;
using System.Text.Json.Nodes;

namespace VSDirector.Data;

public readonly record struct CreationParticleBox(CreationVector Min, CreationVector Max);
public readonly record struct CreationParticleCell(bool Solid, bool Liquid, double RainHeight, CreationVector Color, CreationParticleBox[]? Boxes = null);
public interface ICreationParticleEnvironment
{
    CreationVector Wind { get; }
    // World voxel coordinates = project coordinates + GridOrigin.
    CreationVector GridOrigin => default;
    CreationParticleCell Cell(int x, int y, int z);
}
public sealed class CreationEmptyParticleEnvironment : ICreationParticleEnvironment
{
    public CreationVector Wind => default;
    public CreationParticleCell Cell(int x,int y,int z) => new(false,false,double.NegativeInfinity,CreationVector.One);
}
public sealed record CreationSimulatedParticle(Guid ObjectId, Guid EmitterId, long Index, CreationVector Position,
    double Size, double Alpha, CreationVector Color, double Glow, bool Cube, bool Soft, bool Additive, string Texture, double Rotation = 0,
    CreationVector ShapeScale = default, Quaternion ShapeRotation = default, double LegacyProgress = -1);
public sealed class CreationParticleBudgetException(string message) : InvalidOperationException(message);

/// <summary>Fixed 120 Hz simulation. A render never advances it; all views consume the same completed frame.</summary>
public sealed class DirectorCreationParticleSimulation
{
    public const double Step = 1d/120;
    public const int EffectBudget = 6000, ProjectBudget = 24000, MaxDepth = 8;
    private readonly DirectorCreationProject project;
    private readonly Func<Guid,double,Matrix4x4> transform;
    private readonly ICreationParticleEnvironment environment;
    // Lock each queried input for the lifetime of this rebuild, including backwards seeks.
    private readonly Dictionary<(int,int,int),CreationParticleCell> cells = new();
    private readonly CreationVector wind;
    private readonly List<(CreationSceneObject Object,CreationParticleEmitter Emitter,Definition Definition)> roots = new();
    private List<Live> live = new();
    private readonly SortedDictionary<long,List<Live>> snapshots = new();
    private long tick = -1, nextId;
    private readonly Dictionary<long,long> snapshotIds = new();
    private readonly Dictionary<JsonObject,int> samples=new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<long,Dictionary<JsonObject,int>> snapshotSamples=new();
    private long target;
    private bool cancelled;
    public double Time => Math.Max(0,tick)*Step;
    public double Progress => target <= 0 ? 1 : Math.Clamp((double)Math.Max(0,tick)/target,0,1);
    public bool Ready => tick == target && !cancelled && Error.Length==0;
    public string Error { get; private set; } = "";
    public IReadOnlyList<CreationSimulatedParticle> Particles { get; private set; } = [];
    public DirectorCreationParticleSimulation(DirectorCreationProject project, Func<Guid,double,Matrix4x4> transform, ICreationParticleEnvironment? environment = null)
    {
        this.project=project;this.transform=transform;this.environment=environment??new CreationEmptyParticleEnvironment();wind=this.environment.Wind;
        foreach(var obj in project.Objects.Where(o=>o.Kind==CreationObjectKind.Particle && o.ParticleEffect is not null))
        {
            obj.ParticleEffect!.Validate();
            foreach(var e in obj.ParticleEffect.Emitters.Where(e=>e.Enabled)) roots.Add((obj,e,new(e.Properties,samples)));
        }
    }
    public void Cancel() { cancelled=true;Particles=[]; }
    public bool Seek(double time, int maxSteps = 600)
    {
        if (!double.IsFinite(time) || time<0 || time>86400) throw new ArgumentOutOfRangeException(nameof(time));
        target=(long)Math.Floor(time/Step+1e-7);
        if(cancelled || Error.Length>0)return false;
        if(target<tick)
        {
            long saved=snapshots.Keys.Where(k=>k<=target).DefaultIfEmpty(-1).Max();
            live=saved<0?new():snapshots[saved].Select(p=>p.Copy()).ToList();tick=saved;nextId=saved<0?0:snapshotIds[saved];
            samples.Clear();if(saved>=0)foreach(var pair in snapshotSamples[saved])samples.Add(pair.Key,pair.Value);
        }
        try
        {
            var work=Stopwatch.StartNew();
            for(int i=0;tick<target && i<maxSteps && (i==0||work.ElapsedMilliseconds<8);i++)
            {
                tick++; double now=tick*Step;
                if(tick>0)Advance(now);
                foreach(var (obj,e,d) in roots)
                {
                    if(e.Legacy is not null)continue;
                    if(e.Start>obj.ParticleDuration)continue;
                    double start=obj.ParticleStart+e.Start;
                    long first=Math.Max(0,(long)Math.Ceiling(((tick==0?0:(tick-1)*Step)-start)/e.Interval-1e-8));
                    long last=(long)Math.Floor((now-start)/e.Interval+1e-8);
                    for(long emission=first;emission<=last;emission++)
                    {
                        double born=start+emission*e.Interval;
                        if(born<0 || tick>0 && born<=(tick-1)*Step+1e-8 || e.Burst&&emission>0 || !e.Burst&&born>=start+Math.Max(0,Math.Min(e.Duration,obj.ParticleDuration-e.Start))-1e-8)continue;
                        var matrix=transform(obj.Id,born);
                        var pos=CreationVector.From(Vector3.Transform(e.Offset.Float(),matrix));
                        var previous=transform(obj.Id,Math.Max(0,born-Step));
                        var parentVelocity=CreationVector.From(matrix.Translation-previous.Translation)*(1/Step);
                        Spawn(obj,e,d,pos,parentVelocity,born,0,Seed(obj.ParticleEffect!.Seed,emission,e.Id.GetHashCode()),matrix);
                    }
                }
                if(tick%120==0)
                {
                    snapshots[tick]=live.Select(p=>p.Copy()).ToList();snapshotIds[tick]=nextId;snapshotSamples[tick]=new(samples,ReferenceEqualityComparer.Instance);
                    while(snapshots.Count>24) { long oldest=snapshots.Keys.First(); snapshots.Remove(oldest);snapshotIds.Remove(oldest);snapshotSamples.Remove(oldest); }
                }
            }
            if(tick==target)BuildOutput(time);
            return Ready;
        }
        catch(CreationParticleBudgetException e){ Error=e.Message;Particles=[];throw; }
    }
    private CreationParticleCell Cell(CreationVector pos)
    {
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
        var rng=new Rng(seed);double quantity=Math.Max(0,d.Sample("Quantity",ref rng,1));
        int count=(int)Math.Min(100000,Math.Floor(quantity)+(rng.Next()<quantity-Math.Floor(quantity)?1:0));
        if(live.Count+count>ProjectBudget || live.Count(p=>p.Object.Id==obj.Id)+count>EffectBudget)
            throw new CreationParticleBudgetException($"{obj.Name}: particle budget exceeded ({EffectBudget} per effect / {ProjectBudget} per project).");
        for(int i=0;i<count;i++)
        {
            var prng=new Rng(Seed((int)seed,i,depth));
            var pos=d.Vector("PosOffset",ref prng);
            var velocity=d.Vector("Velocity",ref prng);
            if(depth==0){pos=CreationVector.From(Vector3.TransformNormal(pos.Float(),matrix));velocity=CreationVector.From(Vector3.TransformNormal(velocity.Float(),matrix));}
            pos+=origin;
            var hsva=d.Vector4("HsvaColor",ref prng);
            var color=Hsv(hsva.X,hsva.Y,hsva.Z);
            if(d.Flag("ColorByBlock"))color=Cell(pos).Color;
            double life=Math.Max(.001,d.Sample("LifeLength",ref prng,1)*5);
            double[] nextChildren=d.Secondary.Select(child=>born+Math.Max(Step,child.Sample("SecondarySpawnInterval",ref prng))).ToArray();
            live.Add(new(){Id=nextId++,Object=obj,Emitter=e,Def=d,Position=pos,Velocity=velocity,StartingVelocity=velocity,
                Inherited=inherited*e.ParentVelocityWeight, Born=born,Life=life,Size=d.Sample("Size",ref prng,1),
                Gravity=d.Sample("GravityEffect",ref prng,1)*12,Color=color,Alpha=hsva.W/255,Depth=depth,Seed=prng.State,NextChildren=nextChildren,
                Matrix=matrix});
        }
    }
    private void Advance(double now)
    {
        var children=new List<(Live Parent,Definition Def,bool Death,int Child)>();
        for(int i=live.Count-1;i>=0;i--)
        {
            var p=live[i];double dt=Math.Min(Step,Math.Max(0,now-p.Born));
            var d=p.Def;var old=p.Position;var cell=Cell(old);
            bool death=now-p.Born>=p.Life || d.Flag("DieInAir")&&!cell.Liquid || d.Flag("DieInLiquid")&&cell.Liquid
                || d.Flag("DieOnRainHeightmap")&&old.Y<cell.RainHeight;
            if(!death)
            {
                if(d.Flag("SelfPropelled")&&d.Flag("TerrainCollision",true))p.Velocity+=(p.StartingVelocity-p.Velocity)*(1-Math.Pow(.98,dt*30));
                p.Velocity+=new CreationVector(0,-p.Gravity*dt,0);
                double f=Math.Clamp((now-p.Born)/p.Life,0,1);
                var velocity=p.Velocity;
                if(d.VelocityEvolve is { } ve)velocity=new(velocity.X*Evolve(ve[0],0,f),velocity.Y*Evolve(ve[1],0,f),velocity.Z*Evolve(ve[2],0,f));
                velocity+=p.Inherited+wind*d.Number("WindAffectednes");
                var desired=old+velocity*dt;
                if(d.Flag("TerrainCollision",true))
                {
                    // Swept, axis-separated substeps avoid tunnelling through thin collision boxes.
                    double radius=Math.Clamp(Math.Abs(EvolveValue(d.Node("SizeEvolve"),p.Size,f))*.125,.001,2);
                    int substeps=Math.Clamp((int)Math.Ceiling(Math.Max(Math.Abs(desired.X-old.X),Math.Max(Math.Abs(desired.Y-old.Y),Math.Abs(desired.Z-old.Z)))/.05),1,256);
                    var delta=(desired-old)*(1d/substeps);desired=old;
                    for(int sub=0;sub<substeps;sub++)
                    {
                        var test=desired with{X=desired.X+delta.X};
                        if(Collides(test,radius)){delta=delta with{X=0};p.Velocity=p.Velocity with{X=-p.Velocity.X*d.Number("Bounciness")*.65};}else desired=test;
                        test=desired with{Y=desired.Y+delta.Y};
                        if(Collides(test,radius)){delta=delta with{Y=0};p.Velocity=p.Velocity with{Y=-p.Velocity.Y*d.Number("Bounciness")*.65};}else desired=test;
                        test=desired with{Z=desired.Z+delta.Z};
                        if(Collides(test,radius)){delta=delta with{Z=0};p.Velocity=p.Velocity with{Z=-p.Velocity.Z*d.Number("Bounciness")*.65};}else desired=test;
                    }
                }
                if(d.Flag("SwimOnLiquid")&&Cell(desired).Liquid&&velocity.Y<0){desired=desired with{Y=Math.Floor(desired.Y+environment.GridOrigin.Y)+1-environment.GridOrigin.Y};p.Velocity=p.Velocity with{Y=0};}
                p.Position=desired;
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
        {var p=c.Parent;Spawn(p.Object,p.Emitter,c.Def,p.Position,p.Velocity,now,p.Depth+1,Seed((int)p.Seed,tick,c.Child+(c.Death?10000:0)),p.Matrix);}
    }
    private void BuildOutput(double time)
    {
        var output=new List<CreationSimulatedParticle>();
        foreach(var p in live)
        {
            double f=Math.Clamp((Time-p.Born)/p.Life,0,1);var d=p.Def;
            double size=Math.Max(0,EvolveValue(d.Node("SizeEvolve"),p.Size,f))*.25;
            double alpha=Math.Clamp(EvolveValue(d.Node("OpacityEvolve"),p.Alpha*255,f)/255,0,1);
            var color=new CreationVector(Channel("RedEvolve",p.Color.X),Channel("GreenEvolve",p.Color.Y),Channel("BlueEvolve",p.Color.Z));
            double Channel(string key,double value)=>HasEvolution(d.Node(key))?Math.Clamp(value+Evolve(d.Node(key),value*255,f)/255,0,1):value;
            var pos=p.Position;
            if(p.Emitter.LocalFollow && Matrix4x4.Invert(p.Matrix,out var inverse))pos=CreationVector.From(Vector3.Transform(Vector3.Transform(pos.Float(),inverse),transform(p.Object.Id,time)));
            output.Add(new(p.Object.Id,p.Emitter.Id,p.Id,pos,size,alpha,color,(d.Number("VertexFlags")%256)/255,d.Model=="Cube",p.Emitter.SoftEdge,p.Emitter.Additive,p.Emitter.Texture));
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
        Particles=output;
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
        public Definition[] Secondary,Death;
        public JsonArray? VelocityEvolve;
        public string Model;
        public Definition(JsonObject p,Dictionary<JsonObject,int> samples){this.samples=samples;properties=(JsonObject)p.DeepClone();
            // Missing native fields use the game defaults; authoring defaults remain intentionally simple.
            if(Node("HsvaColor") is null && !p.Any(k=>k.Key.Equals("HsvaColor",StringComparison.OrdinalIgnoreCase)))properties["HsvaColor"]=new JsonArray(CreationParticleFields.Nat(128,128),CreationParticleFields.Nat(128,128),CreationParticleFields.Nat(128,128),CreationParticleFields.Nat(255));
            if(Node("Velocity") is null)properties["Velocity"]=new JsonArray(CreationParticleFields.Nat(0,.5),CreationParticleFields.Nat(0,.5),CreationParticleFields.Nat(0,.5));Secondary=Children("SecondaryParticles");Death=Children("DeathParticles");VelocityEvolve=Node("VelocityEvolve") as JsonArray;if(VelocityEvolve is not null&&VelocityEvolve.All(n=>n is null))VelocityEvolve=null;Model=Node("ParticleModel")?.ToString()??"Cube";if(Model=="0")Model="Quad";if(Model=="1")Model="Cube";}
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
        public CreationVector Position,Velocity,StartingVelocity,Inherited,Color;public double Born,Life,Size,Alpha,Gravity,Wander,Direction=1;
        public double[] NextChildren=[];public ulong RandomState;
        public int Depth;public ulong Seed;public Matrix4x4 Matrix;
        public Live Copy(){var copy=(Live)MemberwiseClone();copy.NextChildren=(double[])NextChildren.Clone();return copy;}
    }
}
