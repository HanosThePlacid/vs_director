using System.Numerics;
using System.Text.Json.Nodes;

namespace VSDirector.Data;

public enum ParticleTrigger { Root, Birth, Periodic, Collision, Death }
public enum ParticleGeometry { Particle, Trail, Beam, Lightning }
public enum ParticleSpawnShape { Point, Box, Sphere, Hemisphere, Disc, Cylinder, Cone, Path, Mesh }
public enum ParticleDirection { Velocity, Outwards, Inwards }
public enum ParticleFacing { Camera, CameraY, Velocity, X, Y, Z }
public enum ParticleValueMode { Constant, Curve, Expression }
public enum ParticleCurveMode { Linear, Step, Bezier }
public enum ParticleClock { Emission, Life, Project }

public sealed class ParticleCurvePoint
{
    public double Time { get; set; }
    public double Value { get; set; }
    public double In { get; set; }
    public double Out { get; set; }
}
public sealed class ParticleCurve
{
    public ParticleCurveMode Mode { get; set; }
    public List<ParticleCurvePoint> Points { get; set; } = [new(){Time=0,Value=1},new(){Time=1,Value=1}];
    public double Sample(double time)
    {
        if(Points.Count==0)return 0;
        if(time<=Points[0].Time)return Points[0].Value;
        for(int i=1;i<Points.Count;i++)if(time<Points[i].Time)
        {
            var a=Points[i-1];var b=Points[i];double dt=b.Time-a.Time,t=(time-a.Time)/dt;
            return Mode switch { ParticleCurveMode.Step=>a.Value,ParticleCurveMode.Bezier=>
                (2*t*t*t-3*t*t+1)*a.Value+(t*t*t-2*t*t+t)*dt*a.Out+(-2*t*t*t+3*t*t)*b.Value+(t*t*t-t*t)*dt*b.In,
                _=>a.Value+(b.Value-a.Value)*t};
        }
        return Points[^1].Value;
    }
    public void Validate()
    {
        if(!Enum.IsDefined(Mode)||Points is null||Points.Count is <1 or >256)throw new InvalidDataException("Invalid particle curve.");
        double previous=double.NegativeInfinity;
        foreach(var p in Points)
        {if(!double.IsFinite(p.Time+p.Value+p.In+p.Out)||p.Time<=previous)throw new InvalidDataException("Curve keys must be finite and strictly ordered.");previous=p.Time;}
    }
}
public sealed class ParticleGradientStop
{
    public double Time {get;set;}
    public CreationVector Color {get;set;}=CreationVector.One;
    public double Alpha {get;set;}=1;
}
public sealed class ParticleGradient
{
    public bool Enabled {get;set;}
    public List<ParticleGradientStop> Stops {get;set;}=[new(),new(){Time=1,Alpha=0}];
    public (CreationVector Color,double Alpha) Sample(double age)
    {
        var a=Stops[0];
        foreach(var b in Stops.Skip(1))
        {if(age<=b.Time){double t=Math.Clamp((age-a.Time)/Math.Max(1e-9,b.Time-a.Time),0,1);return(a.Color*(1-t)+b.Color*t,a.Alpha*(1-t)+b.Alpha*t);}a=b;}
        return(a.Color,a.Alpha);
    }
}
public sealed class ParticleValue
{
    public ParticleValueMode Mode {get;set;}
    public ParticleClock Clock {get;set;}
    public double Constant {get;set;}=1;
    public ParticleCurve Curve {get;set;}=new();
    public string Expression {get;set;}="1";
    public double Evaluate(ParticleEvaluation c)
    {
        double value=Mode switch {ParticleValueMode.Curve=>Curve.Sample(Clock switch {ParticleClock.Life=>c.Age,ParticleClock.Project=>c.Time,_=>c.EmitterTime}),
            ParticleValueMode.Expression=>ParticleExpression.Compile(Expression).Evaluate(c),_=>Constant};
        if(!double.IsFinite(value)||Math.Abs(value)>1e9)throw new InvalidDataException("Particle parameter produced a non-finite or excessive value.");
        return value;
    }
}
public readonly record struct ParticleEvaluation(double Time,double EmitterTime,double Age,double Life,CreationVector Velocity,double Seed,CreationVector Wind,double Temperature=20,double Rain=0);

/// <summary>Director-only authoring modules; native properties remain losslessly stored separately.</summary>
public sealed class CreationParticleModules
{
    public ParticleGeometry Geometry {get;set;}
    public ParticleSpawnShape Shape {get;set;}
    public ParticleDirection Direction {get;set;}
    public CreationVector Extent {get;set;}=CreationVector.One;
    public bool Surface {get;set;}
    public double DirectionSpeed {get;set;}=1;
    public List<CreationVector> Path {get;set;}=[new(0,0,0),new(0,1,0)];
    // Triangle list captured from the selected game mesh, in local block coordinates.
    public List<CreationVector> Mesh {get;set;}=[];
    public string MeshSource {get;set;}="";
    public bool MeshParticle {get;set;}
    public bool FadeEnds {get;set;}
    public ParticleCurve RibbonProfile {get;set;}=new();
    public Guid BindObject {get;set;}
    public Guid BindBone {get;set;}
    public double LoopPeriod {get;set;}
    public double DistanceInterval {get;set;}
    public List<double> Bursts {get;set;}=[];
    public double Probability {get;set;}=1;
    public double Delay {get;set;}
    public double InheritVelocity {get;set;}=1;
    public CreationVector Acceleration {get;set;}
    public double Drag {get;set;}
    public CreationVector ForceCenter {get;set;}
    public double Attraction {get;set;}
    public double Vortex {get;set;}
    public double Noise {get;set;}
    public bool Parametric {get;set;}
    public double Spin {get;set;}
    public double SpinAcceleration {get;set;}
    public double InitialRotation {get;set;}
    public double Friction {get;set;}
    public bool KillPlane {get;set;}
    public double KillHeight {get;set;}
    public string ExpireBlocks {get;set;}="";
    public bool ExpireOutsideBlocks {get;set;}
    public string ColorBlock {get;set;}="";
    public ParticleFacing Facing {get;set;}
    public double Width {get;set;}=1;
    public double Height {get;set;}=1;
    public CreationVector UvOrigin {get;set;}
    public CreationVector UvSize {get;set;}=CreationVector.One;
    public int Columns {get;set;}=1;
    public int Rows {get;set;}=1;
    public int Frames {get;set;}=1;
    public double FramesPerSecond {get;set;}=12;
    public bool AnimateByLife {get;set;}
    public bool TextureLoop {get;set;}=true;
    public ParticleGradient Gradient {get;set;}=new();
    public Dictionary<string,ParticleValue> Drivers {get;set;}=new(StringComparer.Ordinal);
    public CreationVector End {get;set;}=new(0,2,0);
    public Guid EndObject {get;set;}
    public Guid EndBone {get;set;}
    public double RibbonWidth {get;set;}=.05;
    public double Retain {get;set;}=1;
    public Guid TrailEmitter {get;set;}
    public double SampleDistance {get;set;}=.05;
    public int Segments {get;set;}=16;
    public double Bend {get;set;}
    public double Jitter {get;set;}=.15;
    public int Branches {get;set;}=2;
    public double FlickerRate {get;set;}=12;
    public double TextureFlow {get;set;}
    public bool ClipAtTerrain {get;set;}
    public double Value(string key,double fallback,ParticleEvaluation c,ParticleClock? only=null)
        =>Drivers.TryGetValue(key,out var v)&&(!only.HasValue||v.Clock==only)?v.Evaluate(c):fallback;
    public void Validate()
    {
        if(!Enum.IsDefined(Geometry)||!Enum.IsDefined(Shape)||!Enum.IsDefined(Direction)||!Enum.IsDefined(Facing)
            ||!Extent.Finite()||!Acceleration.Finite()||!ForceCenter.Finite()||!End.Finite()||!UvOrigin.Finite()||!UvSize.Finite()
            ||Path is null||Path.Count>1024||Path.Any(p=>!p.Finite())||Mesh is null||Mesh.Count>49152||Mesh.Count%3!=0||Mesh.Any(p=>!p.Finite())
            ||Bursts is null||Bursts.Count>4096||Bursts.Any(t=>!double.IsFinite(t)||t<0)||Drivers is null||Drivers.Count>128
            ||Columns is <1 or >256||Rows is <1 or >256||Frames<1||Frames>Columns*Rows||Segments is <2 or >512||Branches is <0 or >16)
            throw new InvalidDataException("Invalid particle module.");
        if(RibbonProfile is null)throw new InvalidDataException("Missing ribbon profile.");RibbonProfile.Validate();
        foreach(double n in new[]{DirectionSpeed,LoopPeriod,DistanceInterval,Probability,Delay,InheritVelocity,Drag,Attraction,Vortex,Noise,Spin,SpinAcceleration,InitialRotation,Friction,KillHeight,Width,Height,FramesPerSecond,RibbonWidth,Retain,SampleDistance,Bend,Jitter,FlickerRate,TextureFlow})
            if(!double.IsFinite(n)||Math.Abs(n)>1e6)throw new InvalidDataException("Invalid particle module number.");
        if(Friction is <0 or >1||FramesPerSecond<0||FlickerRate<0||Jitter<0||Probability is <0 or >1||Delay<0||LoopPeriod<0||DistanceInterval<0||Retain<0||SampleDistance<=0||RibbonWidth<0||Drag<0||Width<0||Height<0)
            throw new InvalidDataException("Particle module value out of range.");
        foreach(var (key,v) in Drivers)
        {if(!ParticleParameters.All.Any(p=>p.Id==key)||v is null||!Enum.IsDefined(v.Mode)||!Enum.IsDefined(v.Clock)||!double.IsFinite(v.Constant))throw new InvalidDataException("Invalid particle driver: "+key);
            if(v.Curve is null)throw new InvalidDataException("Missing curve.");v.Curve.Validate();if(v.Mode==ParticleValueMode.Expression)ParticleExpression.Compile(v.Expression);}
        if(Gradient?.Stops is null||Gradient.Stops.Count is <2 or >128)throw new InvalidDataException("Invalid gradient.");
        double last=-1;
        foreach(var s in Gradient.Stops){if(!s.Color.Finite()||!double.IsFinite(s.Alpha+s.Time)||s.Time<=last||s.Time is <0 or >1||s.Alpha is <0 or >1)throw new InvalidDataException("Invalid gradient stop.");last=s.Time;}
    }
}
public sealed record ParticleParameter(string Id,string Unit,ParticleClock DefaultClock);
public static class ParticleParameters
{
    public static readonly ParticleParameter[] All = [
        new("Quantity","particles",ParticleClock.Emission),new("LifeLength","native lifetime",ParticleClock.Emission),
        new("Size","native size",ParticleClock.Life),new("Alpha","0–1",ParticleClock.Life),
        new("Color.R","0–1",ParticleClock.Life),new("Color.G","0–1",ParticleClock.Life),new("Color.B","0–1",ParticleClock.Life),
        new("Velocity.X","blocks/s",ParticleClock.Emission),new("Velocity.Y","blocks/s",ParticleClock.Emission),new("Velocity.Z","blocks/s",ParticleClock.Emission),
        new("Position.X","blocks",ParticleClock.Life),new("Position.Y","blocks",ParticleClock.Life),new("Position.Z","blocks",ParticleClock.Life),
        new("GravityEffect","multiplier",ParticleClock.Emission),new("Spin","degrees/s",ParticleClock.Life),
        new("Width","multiplier",ParticleClock.Life),new("Height","multiplier",ParticleClock.Life),new("RibbonWidth","blocks",ParticleClock.Life),
        new("Interval","s",ParticleClock.Emission),new("Glow","0–1",ParticleClock.Life)];
}

public static class ParticleCompatibility
{
    public static IReadOnlyList<string> Issues(CreationParticleEffect effect)
    {
        var issues=new List<string>();
        if(effect.GameSpeed!=1||effect.Density!=1)issues.Add("Director game speed / density");
        foreach(var e in effect.AllNodes())
        {
            void Add(string s)=>issues.Add(e.Name+": "+s);
            if(e.Legacy is not null)Add("legacy evaluator");
            if(e.Recipe!=CreationParticleRecipe.None)Add("reference recipe: "+e.Recipe);
            if(e.Trigger is ParticleTrigger.Birth or ParticleTrigger.Collision)Add("Director event");
            if(e.Modules is {} m)
            {
                if(m.Geometry!=ParticleGeometry.Particle)Add(m.Geometry.ToString());
                var baseline=new CreationParticleModules();
                var actual=System.Text.Json.JsonSerializer.SerializeToNode(m,CreationJson.Options)!.AsObject();
                var defaults=System.Text.Json.JsonSerializer.SerializeToNode(baseline,CreationJson.Options)!.AsObject();
                var changed=actual.Where(pair=>!JsonNode.DeepEquals(pair.Value,defaults[pair.Key])).Select(pair=>pair.Key).ToArray();
                if(changed.Length>0)Add("Director: "+string.Join(", ",changed));
            }
            if(e.Texture.Length>0||e.Additive||e.LocalFollow||e.ParentVelocityWeight!=0)Add("Director material / space / inherited velocity");
            if(e.Start!=0||e.Interval!=.1||e.Duration!=2||e.Burst||e.Offset!=default)Add("Director emission schedule / offset");
            foreach(var w in CreationParticleFields.Warnings(e.Properties,false))Add(w);
        }
        return issues;
    }
}
