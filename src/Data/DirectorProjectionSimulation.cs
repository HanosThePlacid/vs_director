using System.Diagnostics;
using System.Numerics;

namespace VSDirector.Data;

/// <summary>Deterministic visual physics in projection-local coordinates. Rendering consumes Frame only.</summary>
public sealed class DirectorProjectionSimulation
{
    public const double Step=1d/120;
    public const int Budget=6000;
    private sealed record Body(Vector3 Offset,Vector3 Velocity,Vector3 Angles,double Impact);
    private readonly IReadOnlyList<ProjectionCell> source;
    private readonly CreationProjectionSettings settings;
    private readonly ICreationParticleEnvironment environment;
    private readonly Func<double,Matrix4x4> world;
    private readonly Dictionary<(int,int,int),CreationParticleCell> environmentCache=[];
    private readonly SortedDictionary<long,Dictionary<int,Body>> snapshots=[];
    private Dictionary<int,Body> bodies=[];
    private long tick=-1,target;
    private bool cancelled;
    private IEnumerator<ProjectionCellState>? staticWork;
    private readonly List<ProjectionCellState> staticFrame=[];
    private double staticTime=-1;
    private int queries;
    public bool Ready {get;private set;}
    public string Error {get;private set;}="";
    public double Progress=>target<=0?1:Math.Clamp((double)Math.Max(0,tick)/target,0,1);
    public IReadOnlyList<ProjectionCellState> Frame {get;private set;}=[];
    public DirectorProjectionSimulation(IReadOnlyList<ProjectionCell> source,CreationProjectionSettings settings,ICreationParticleEnvironment? environment=null,Func<double,Matrix4x4>? world=null)
    {this.source=source;this.settings=CreationJson.Clone(settings);this.environment=environment??new CreationEmptyParticleEnvironment();this.world=world??(_=>Matrix4x4.Identity);}
    public void Cancel(){cancelled=true;Ready=false;staticWork?.Dispose();staticWork=null;}
    public bool Seek(double time)
    {
        if(cancelled)return false;
        if(!double.IsFinite(time)||time<0||time>86400)throw new ArgumentOutOfRangeException(nameof(time));
        bool physics=settings.V("physicsEnabled",0)>.5||settings.Parameters.GetValueOrDefault("physicsEnabled")?.Track.Keys.Any(k=>k.Value.X>.5)==true;
        if(!physics)
        {
            if(Ready&&(!settings.Animated||staticTime==time))return true;
            if(staticTime!=time){staticWork?.Dispose();staticWork=DirectorProjectionEvaluator.Enumerate(source,settings,time).GetEnumerator();staticFrame.Clear();staticTime=time;}
            Ready=false;var timer=Stopwatch.StartNew();
            while(staticWork is not null&&timer.ElapsedMilliseconds<6)
            {
                if(!staticWork.MoveNext()){staticWork.Dispose();staticWork=null;Frame=staticFrame.ToArray();Ready=true;return true;}
                staticFrame.Add(staticWork.Current);
            }
            return false;
        }
        target=(long)Math.Floor(time/Step+1e-7);Ready=false;
        if(target<tick)
        {
            tick=snapshots.Keys.Where(t=>t<=target).DefaultIfEmpty(-1).Max();bodies=tick<0?[]:new(snapshots[tick]);
        }
        var watch=Stopwatch.StartNew();
        try
        {
            while(tick<target&&(watch.ElapsedMilliseconds<6||tick<0))
            {
                tick++;queries=0;double now=tick*Step;
                if(settings.V("physicsEnabled",now)<.5){bodies.Clear();continue;}
                if(source.Count+settings.Edits.Count(e=>e.Key<0)>Budget)throw new InvalidDataException($"Structure physics budget exceeded: more than {Budget} cells. Split the structure before enabling physics.");
                var frame=DirectorProjectionEvaluator.Evaluate(source,settings,now);var active=frame.Where(s=>s.Visible).ToArray();
                if(active.Length>Budget)throw new InvalidDataException($"Structure physics budget exceeded: {active.Length}/{Budget} cells.");
                var ids=active.Select(s=>s.Cell.Id).ToHashSet();foreach(int id in bodies.Keys.Where(id=>!ids.Contains(id)).ToArray())bodies.Remove(id);
                var matrix=world(now);if(!Matrix4x4.Invert(matrix,out var inverse))throw new InvalidDataException("Cannot simulate a structure with a singular transform.");
                double separation=settings.V("separation",now),impact=settings.V("impact",now),drag=settings.V("drag",now),spin=settings.V("spin",now),gravity=settings.V("gravity",now),bounce=settings.V("bounce",now);
                foreach(var cell in active)
                {
                    int id=cell.Cell.Id;var body=bodies.GetValueOrDefault(id,new(Vector3.Zero,Vector3.Zero,Vector3.Zero,0));
                    var direction=cell.Cell.Position.Float()+new Vector3(.5f)-settings.Size.Float()*.5f;
                    if(direction.LengthSquared()<1e-8)direction=new((float)DirectorProjectionEditing.Random(id,1)-.5f,1,(float)DirectorProjectionEditing.Random(id,2)-.5f);
                    direction=Vector3.Normalize(direction);
                    var v=body.Velocity+direction*(float)(Math.Max(0,impact-body.Impact)+separation*Step);
                    v+=Vector3.TransformNormal(new Vector3(0,(float)(-gravity*Step),0),inverse);v*=(float)Math.Exp(-drag*Step);
                    var center=Vector3.Transform(new Vector3(.5f),cell.Matrix)+body.Offset;
                    var next=body.Offset;var delta=v*(float)Step;
                    int pieces=Math.Clamp((int)Math.Ceiling(delta.Length()/.2f),1,128);
                    for(int n=0;n<pieces;n++)for(int axis=0;axis<3;axis++)
                    {
                        var d=axis==0?new Vector3(delta.X/pieces,0,0):axis==1?new Vector3(0,delta.Y/pieces,0):new Vector3(0,0,delta.Z/pieces);
                        var pos=Vector3.Transform(center+(next-body.Offset)+d,matrix);
                        // Conservative AABB of the transformed cell; sweep small steps to prevent tunnelling.
                        var shape=cell.Matrix*CreationTransform.RotationMatrix(CreationVector.From(body.Angles))*matrix;
                        var extent=new Vector3(Math.Abs(shape.M11)+Math.Abs(shape.M21)+Math.Abs(shape.M31),Math.Abs(shape.M12)+Math.Abs(shape.M22)+Math.Abs(shape.M32),Math.Abs(shape.M13)+Math.Abs(shape.M23)+Math.Abs(shape.M33))*.499f;
                        if(Collides(pos,extent))
                        {
                            if(axis==0){v.X=-(float)bounce*v.X;delta.X=0;}else if(axis==1){v.Y=-(float)bounce*v.Y;delta.Y=0;}else{v.Z=-(float)bounce*v.Z;delta.Z=0;}
                        }
                        else next+=d;
                    }
                    var angular=new Vector3((float)DirectorProjectionEditing.Random(id,11)*2-1,(float)DirectorProjectionEditing.Random(id,12)*2-1,(float)DirectorProjectionEditing.Random(id,13)*2-1);
                    bodies[id]=new(next,v,body.Angles+angular*(float)(spin*Step),impact);
                }
                if(tick%120==0){snapshots[tick]=new(bodies);while(snapshots.Count>16)snapshots.Remove(snapshots.Keys.First());}
            }
            if(tick!=target)return false;
            Frame=DirectorProjectionEvaluator.Evaluate(source,settings,time).Select(s=>
            {
                if(!bodies.TryGetValue(s.Cell.Id,out var b)||settings.V("physicsEnabled",time)<.5)return s;
                var center=Vector3.Transform(new Vector3(.5f),s.Matrix);
                return s with{Matrix=s.Matrix*Matrix4x4.CreateTranslation(-center)*CreationTransform.RotationMatrix(CreationVector.From(b.Angles))*Matrix4x4.CreateTranslation(center+b.Offset)};
            }).ToArray();Ready=true;return true;
        }
        catch(Exception e){Error=e.Message;throw;}
    }
    private bool Collides(Vector3 point,Vector3 half)
    {
        var origin=environment.GridOrigin.Float();var a=point-half+origin;var b=point+half+origin;
        for(int x=(int)Math.Floor(a.X);x<=Math.Floor(b.X);x++)for(int y=(int)Math.Floor(a.Y);y<=Math.Floor(b.Y);y++)for(int z=(int)Math.Floor(a.Z);z<=Math.Floor(b.Z);z++)
        {
            if(++queries>250000)throw new InvalidDataException("Structure collision query budget exceeded. Reduce scale or split the structure.");
            var key=(x,y,z);if(!environmentCache.TryGetValue(key,out var cell))environmentCache[key]=cell=environment.Cell(x,y,z);
            if(!cell.Solid)continue;
            foreach(var box in cell.Boxes??[new(default,CreationVector.One)])
                if(a.X<x+box.Max.X&&b.X>x+box.Min.X&&a.Y<y+box.Max.Y&&b.Y>y+box.Min.Y&&a.Z<z+box.Max.Z&&b.Z>z+box.Min.Z)return true;
        }
        return false;
    }
}
