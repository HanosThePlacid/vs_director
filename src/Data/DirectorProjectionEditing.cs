using System.Numerics;
using System.Text.Json.Serialization;

namespace VSDirector.Data;

public sealed record ProjectionCell(int Id,int Source,CreationVector Position,string Code,bool Liquid=false);
public sealed record ProjectionCellState(ProjectionCell Cell,Matrix4x4 Matrix,string Code,bool Visible,double Alpha,double Frontier);
public sealed class ProjectionCellEdit
{
    public int Source {get;set;}
    public CreationVector Offset {get;set;}
    public CreationVector Rotation {get;set;}
    public CreationVector Scale {get;set;}=CreationVector.One;
    public bool Deleted {get;set;}
    public bool OverrideTransform {get;set;}
    public List<CreationTrack> Tracks {get;set;}=[];
}
public sealed class ProjectionChoiceKey
{
    public double Time {get;set;}
    public string Value {get;set;}="";
}
public sealed class ProjectionChoice
{
    public string Value {get;set;}="";
    public List<ProjectionChoiceKey> Keys {get;set;}=[];
    public string At(double time)=>Keys.Where(k=>k.Time<=time).OrderBy(k=>k.Time).LastOrDefault()?.Value??Value;
}
public sealed class ProjectionScalar
{
    public double Value {get;set;}
    public CreationTrack Track {get;set;}=new();
    public double At(double time,bool step=false)
    {
        if(!Track.Enabled||Track.Keys.Count==0)return Value;
        if(step)return Track.Keys.Where(k=>k.Time<=time).OrderBy(k=>k.Time).LastOrDefault()?.Value.X??Value;
        return DirectorCreationEvaluator.Sample(Track,time,new(Value,0,0)).X;
    }
}
public sealed record ProjectionField(string Id,string Group,double Default,double Min,double Max,bool Step=false);
public static class ProjectionFields
{
    public static readonly IReadOnlyList<ProjectionField> All=Build();
    public static readonly IReadOnlyDictionary<string,ProjectionField> ById=All.ToDictionary(f=>f.Id);
    private static List<ProjectionField> Build()
    {
        List<ProjectionField> fields=[new("appear","growth",1,0,1),new("growth","growth",1,0,1),new("seed","growth",1,0,int.MaxValue,true),
            new("fill","growth",1,0,1),new("fall","growth",0,0,1),new("height","growth",8,0,1024),new("rain","growth",0,0,1)];
        foreach(string wave in new[]{"waveA","waveB"}){fields.Add(new(wave+"Enabled","growth",0,0,1,true));fields.Add(new(wave+"Direction","growth",2,0,5,true));fields.Add(new(wave,"growth",1,0,1));}
        for(int i=0;i<5;i++)fields.Add(new("glass"+i,"materials",0,0,1));
        fields.Add(new("blockEdge","materials",.1,0,1));fields.Add(new("blockEdgeRatio","materials",1,0,1));
        for(int i=0;i<4;i++)fields.Add(new("edge"+i,"materials",0,0,1));
        fields.AddRange([new("particlesEnabled","particles",0,0,1,true),new("particleRatio","particles",0,0,1),new("particleCount","particles",8,0,128,true),
            new("particleSpeed","particles",0,-5,5),new("particleLife","particles",20,0,600),new("particleInterval","particles",.1,1d/120,60),
            new("particleOverride","particles",0,0,1,true),new("particleKeepBlocks","particles",0,0,1,true),new("rainbow","particles",0,0,8),
            new("colorStrength","particles",0,0,1),new("colorR","particles",1,0,1),new("colorG","particles",1,0,1),new("colorB","particles",1,0,1),
            new("outline","particles",0,0,1,true),new("outlineR","particles",0,0,1),new("outlineG","particles",0,0,1),new("outlineB","particles",0,0,1),new("particleGlow","particles",0,0,1),
            new("physicsEnabled","physics",0,0,1,true),new("separation","physics",0,0,100),new("drag","physics",1,0,30),new("spin","physics",0,-720,720),
            new("impact","physics",0,0,100),new("gravity","physics",9.81,-100,100),new("bounce","physics",.3,0,1)]);
        return fields;
    }
}
public sealed partial class CreationProjectionSettings
{
    public bool CellKeyframes {get;set;}
    public Dictionary<int,ProjectionCellEdit> Edits {get;set;}=[];
    public Dictionary<string,ProjectionScalar> Parameters {get;set;}=[];
    public ProjectionChoice[] Glass {get;set;}=Enumerable.Range(0,5).Select(_=>new ProjectionChoice()).ToArray();
    public ProjectionChoice[] Edges {get;set;}=Enumerable.Range(0,4).Select(_=>new ProjectionChoice()).ToArray();
    public Dictionary<string,CreationParticleEffect> ParticleSchemes {get;set;}=[];
    public ProjectionChoice ParticleScheme {get;set;}=new();
    public bool RangeEnabled {get;set;}
    public CreationVector RangeMin {get;set;}
    public CreationVector RangeMax {get;set;}
    [JsonIgnore] public bool Edited=>Version==2&&(Edits.Count>0||Parameters.Count>0);
    [JsonIgnore] public bool Animated=>Parameters.Values.Any(v=>v.Track.Enabled&&v.Track.Keys.Count>0)||Glass.Concat(Edges).Any(c=>c.Keys.Count>0)
        ||CellKeyframes&&Edits.Values.Any(e=>e.Tracks.Any(t=>t.Enabled&&t.Keys.Count>0))||V("physicsEnabled",0)>0||Parameters.GetValueOrDefault("physicsEnabled")?.Track.Keys.Count>0;
    public double V(string key,double time=0)
    {
        var f=ProjectionFields.ById[key];return Math.Clamp(Parameters.TryGetValue(key,out var p)?p.At(time,f.Step):f.Default,f.Min,f.Max);
    }
    public ProjectionScalar Parameter(string key)
    {
        Version=2;if(!Parameters.TryGetValue(key,out var p))Parameters[key]=p=new(){Value=ProjectionFields.ById[key].Default};return p;
    }
    public ProjectionCellEdit Edit(ProjectionCell cell)
    {
        Version=2;if(!Edits.TryGetValue(cell.Id,out var edit))Edits[cell.Id]=edit=new(){Source=cell.Source};return edit;
    }
    public void ValidateEditing()
    {
        if(Edits is null||Parameters is null||Glass?.Length!=5||Edges?.Length!=4||ParticleSchemes is null||ParticleScheme is null||!RangeMin.Finite()||!RangeMax.Finite())throw new InvalidDataException("Invalid projection editing data.");
        foreach(var (id,e) in Edits)
        {
            if(e is null||e.Source<0||!e.Offset.Finite()||!e.Rotation.Finite()||!e.Scale.Finite()||e.Scale.X is <.25 or >4||e.Scale.Y is <.25 or >4||e.Scale.Z is <.25 or >4||e.Tracks is null)throw new InvalidDataException("Invalid projection cell edit: "+id);
            foreach(var t in e.Tracks)ValidateTrack(t);
        }
        foreach(var (key,p) in Parameters)
        {
            if(p is null||!ProjectionFields.ById.TryGetValue(key,out var f)||!double.IsFinite(p.Value)||p.Value<f.Min||p.Value>f.Max)throw new InvalidDataException("Invalid projection parameter: "+key);
            ValidateTrack(p.Track);
        }
        foreach(var c in Glass.Concat(Edges).Append(ParticleScheme))
            if(c is null||c.Value is null||c.Keys is null||c.Keys.Any(k=>!double.IsFinite(k.Time)||k.Time<0||k.Time>86400||k.Value is null))throw new InvalidDataException("Invalid projection resource key.");
        foreach(var effect in ParticleSchemes.Values){if(effect is null)throw new InvalidDataException("Invalid structure particle scheme.");effect.Validate();}
    }
    private static void ValidateTrack(CreationTrack t)
    {
        if(t is null||t.Keys is null||t.Keys.Any(k=>!double.IsFinite(k.Time)||k.Time<0||k.Time>86400||!k.Value.Finite()||!Enum.IsDefined(k.Interpolation)||!double.IsFinite(k.HandleInTime+k.HandleInValue+k.HandleOutTime+k.HandleOutValue)))throw new InvalidDataException("Invalid projection keyframe.");
    }
}

/// <summary>Pure structure operations. Source IDs remain stable; duplicated cells use negative IDs.</summary>
public static class DirectorProjectionEditing
{
    public static IEnumerable<ProjectionCell> Cells(IReadOnlyList<ProjectionCell> source,CreationProjectionSettings settings)
    {
        foreach(var (id,e) in settings.Edits)
            if(e.Source>=source.Count||id>=0&&(id>=source.Count||source[id].Source!=e.Source))throw new InvalidDataException("Missing source cell: "+e.Source);
        foreach(var cell in source)yield return cell;
        foreach(var (id,e) in settings.Edits.Where(p=>p.Key<0).OrderByDescending(p=>p.Key))
        {if(e.Source>=source.Count)throw new InvalidDataException("Missing source cell: "+e.Source);yield return source[e.Source] with{Id=id};}
    }
    public static CreationVector Position(ProjectionCell cell,CreationProjectionSettings settings)=>cell.Position+(settings.Edits.GetValueOrDefault(cell.Id)?.Offset??default);
    public static bool InRange(ProjectionCell cell,CreationProjectionSettings settings)
    {
        var p=Position(cell,settings)+new CreationVector(.5,.5,.5);var a=settings.RangeMin;var b=settings.RangeMax;
        return p.X>=Math.Min(a.X,b.X)&&p.Y>=Math.Min(a.Y,b.Y)&&p.Z>=Math.Min(a.Z,b.Z)&&p.X<Math.Max(a.X,b.X)+1&&p.Y<Math.Max(a.Y,b.Y)+1&&p.Z<Math.Max(a.Z,b.Z)+1;
    }
    public static IReadOnlyList<int> Duplicate(IReadOnlyList<ProjectionCell> source,CreationProjectionSettings settings,IReadOnlySet<int> selection)
    {
        var all=Cells(source,settings).Where(c=>settings.Edits.GetValueOrDefault(c.Id)?.Deleted!=true).ToArray();var selected=all.Where(c=>selection.Contains(c.Id)).ToArray();
        if(selected.Length==0)return [];
        var occupied=all.Select(c=>(Position(c,settings),c.Liquid)).ToHashSet();
        CreationVector[] directions=[new(1,0,0),new(-1,0,0),new(0,0,1),new(0,0,-1),new(0,1,0),new(0,-1,0)];
        for(int distance=1;distance<=1024;distance++)foreach(var direction in directions)
        {
            var offset=direction*distance;
            if(selected.Any(c=>occupied.Contains((Position(c,settings)+offset,c.Liquid))))continue;
            List<int> copies=[];int next=Math.Min(0,settings.Edits.Keys.DefaultIfEmpty(0).Min())-1;
            foreach(var cell in selected)
            {
                var edit=settings.Edits.TryGetValue(cell.Id,out var e)?CreationJson.Clone(e):new ProjectionCellEdit{Source=cell.Source};
                edit.Offset+=offset;foreach(var track in edit.Tracks){track.Id=Guid.NewGuid();foreach(var key in track.Keys)key.Id=Guid.NewGuid();}
                settings.Edits[next]=edit;copies.Add(next--);
            }
            settings.Version=2;return copies;
        }
        throw new InvalidOperationException("No empty location for duplicated cells within 1024 blocks.");
    }
    public static IEnumerable<CreationParticleEffect> Effects(CreationSceneObject obj)
    {
        if(obj.ParticleEffect is not null)yield return obj.ParticleEffect;
        if(obj.Projection is not null)foreach(var effect in obj.Projection.ParticleSchemes.Values)yield return effect;
    }
    public static void Remap(CreationSceneObject obj,Func<Guid,Guid> map)
    {
        if(obj.Projection is not {} s)return;
        foreach(var track in s.Parameters.Values.Select(p=>p.Track).Concat(s.Edits.Values.SelectMany(e=>e.Tracks)))
        {track.Id=map(track.Id);track.ObjectId=map(track.ObjectId);track.PartId=map(track.PartId);foreach(var key in track.Keys)key.Id=map(key.Id);}
        foreach(var node in s.ParticleSchemes.Values.SelectMany(e=>e.AllNodes()))
        {
            node.Id=map(node.Id);
            if(node.Modules is {} m){m.BindObject=map(m.BindObject);m.BindBone=map(m.BindBone);m.EndObject=map(m.EndObject);m.EndBone=map(m.EndBone);m.TrailEmitter=map(m.TrailEmitter);}
        }
    }
    public static double Random(int cell,int seed,int salt=0)
    {uint x=unchecked((uint)cell*747796405u+(uint)seed*2891336453u+(uint)salt*277803737u);x=((x>>((int)(x>>28)+4))^x)*277803737u;x=(x>>22)^x;return x/(double)uint.MaxValue;}
    public static CreationVector Edge(int point,int count,double distance)
    {
        double phase=((point*12d/Math.Max(1,count)+distance)%12+12)%12;int edge=(int)phase;double t=phase-edge;
        return edge switch{0=>new(t,0,0),1=>new(1,t,0),2=>new(1-t,1,0),3=>new(0,1-t,0),4=>new(t,0,1),5=>new(1,t,1),6=>new(1-t,1,1),7=>new(0,1-t,1),8=>new(0,0,t),9=>new(1,0,t),10=>new(1,1,t),_=>new(0,1,t)};
    }
}

public static class DirectorProjectionEvaluator
{
    public static IReadOnlyList<ProjectionCellState> Evaluate(IReadOnlyList<ProjectionCell> source,CreationProjectionSettings settings,double time)
        =>Enumerate(source,settings,time).ToArray();
    public static IEnumerable<ProjectionCellState> Enumerate(IReadOnlyList<ProjectionCell> source,CreationProjectionSettings settings,double time)
    {
        if(!double.IsFinite(time)||time<0||time>86400)throw new ArgumentOutOfRangeException(nameof(time));
        var values=ProjectionFields.All.ToDictionary(f=>f.Id,f=>settings.V(f.Id,time));
        double V(string key)=>values[key];
        var glassCodes=settings.Glass.Select(c=>c.At(time)).ToArray();var edgeCodes=settings.Edges.Select(c=>c.At(time)).ToArray();
        int seed=(int)V("seed");double growth=V("growth"),appear=V("appear");
        foreach(var cell in DirectorProjectionEditing.Cells(source,settings))
        {
            var edit=settings.Edits.GetValueOrDefault(cell.Id);var position=DirectorProjectionEditing.Position(cell,settings);
            var scale=edit?.Scale??CreationVector.One;var rotation=edit?.Rotation??default(CreationVector);bool visible=edit?.Deleted!=true;
            double rank=(cell.Position.Y+DirectorProjectionEditing.Random(cell.Id,seed))/Math.Max(1,settings.Size.Y);
            double front=growth-rank;visible&=growth>=1||front>=0;
            double Wave(string name)
            {
                int axis=(int)V(name+"Direction");var p=cell.Position;var size=settings.Size;
                double r=(axis/2) switch{0=>(p.X+.5)/size.X,1=>(p.Y+.5)/size.Y,_=>(p.Z+.5)/size.Z};if(axis%2==1)r=1-r;
                return V(name)-r;
            }
            bool wa=V("waveAEnabled")>.5,wb=V("waveBEnabled")>.5;
            if(wa||wb){double wf=Math.Max(wa?Wave("waveA"):double.NegativeInfinity,wb?Wave("waveB"):double.NegativeInfinity);visible&=wf>=0;front=Math.Min(front,wf);}
            visible&=DirectorProjectionEditing.Random(cell.Id,seed,3)<V("fill")||V("fill")>=1;
            double falling=V("fall");double rain=V("rain");
            if(rain>0)falling=Math.Max(falling,Math.Clamp((DirectorProjectionEditing.Random(cell.Id,seed,4)-rain)/.2+1,0,1));
            position+=new CreationVector(0,falling*V("height"),0);
            if(settings.CellKeyframes&&edit is not null)
            {
                bool over=edit.OverrideTransform;
                var overrideTrack=edit.Tracks.Find(t=>t.Channel==CreationChannel.Pivot);
                if(overrideTrack is {Enabled:true,Keys.Count:>0})over=Step(overrideTrack,time,over?1:0)>.5;
                foreach(var track in edit.Tracks.Where(t=>t.Enabled&&t.Keys.Count>0))
                {
                    var value=DirectorCreationEvaluator.Sample(track,time,default);
                    switch(track.Channel)
                    {
                        case CreationChannel.Position:position=over?value:position+value;break;
                        case CreationChannel.Rotation:rotation=over?value:rotation+value;break;
                        case CreationChannel.Scale:scale=over?value:new(scale.X*value.X,scale.Y*value.Y,scale.Z*value.Z);break;
                        case CreationChannel.Visible:visible&=Step(track,time,1)>.5;break;
                    }
                }
            }
            scale=new(Math.Clamp(scale.X,.25,4),Math.Clamp(scale.Y,.25,4),Math.Clamp(scale.Z,.25,4));
            string code=cell.Code;
            for(int i=0;i<5;i++)if(glassCodes[i] is {Length:>0} glass&&DirectorProjectionEditing.Random(cell.Id,seed,10+i)<V("glass"+i))code=glass;
            if(front>=0&&front<=V("blockEdge"))for(int i=0;i<4;i++)
                if(edgeCodes[i] is {Length:>0} edge&&DirectorProjectionEditing.Random(cell.Id,seed,20+i)<V("edge"+i)*V("blockEdgeRatio"))code=edge;
            var matrix=Matrix4x4.CreateTranslation(-.5f,-.5f,-.5f)*Matrix4x4.CreateScale(scale.Float())*CreationTransform.RotationMatrix(rotation)*Matrix4x4.CreateTranslation((position+new CreationVector(.5,.5,.5)).Float());
            yield return new(cell,matrix,code,visible,appear,front);
        }
    }
    private static double Step(CreationTrack t,double time,double value)=>t.Keys.Where(k=>k.Time<=time).OrderBy(k=>k.Time).LastOrDefault()?.Value.X??value;
}
