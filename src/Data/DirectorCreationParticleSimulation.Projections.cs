using System.Numerics;
using System.Text.Json.Nodes;

namespace VSDirector.Data;

public sealed record ProjectionParticleSource(CreationSceneObject Object,IReadOnlyList<ProjectionCell> Cells,Func<double,IReadOnlyList<ProjectionCellState>>? Frame=null,Func<double,bool>? Prepare=null);

public sealed partial class DirectorCreationParticleSimulation
{
    private readonly List<ProjectionParticleSource> projectionSources=[];
    private readonly Dictionary<Guid,(ProjectionParticleSource Source,int Cell)> projectionAttachments=[];
    private readonly Dictionary<Guid,IReadOnlyList<ProjectionCellState>> projectionFrames=[];
    private readonly Dictionary<Guid,IReadOnlyDictionary<int,ProjectionCellState>> projectionStates=[];
    private readonly Dictionary<(Guid,string,int),(CreationSceneObject Object,CreationParticleEmitter Emitter,Definition Definition)> projectionDefinitions=[];
    public void AddProjection(ProjectionParticleSource source)
    {
        if(tick>=0)throw new InvalidOperationException("Projection particle sources must be registered before seeking.");
        projectionSources.Add(source);
        var cells=source.Cells.ToDictionary(c=>((uint)c.Position.X|((uint)c.Position.Z<<10)|((uint)c.Position.Y<<20))+":"+c.Code,c=>c.Id);
        foreach(var child in project.Objects.Where(o=>o.ParentId==source.Object.Id&&o.ProjectionParticleSource.Length>0))
            if(cells.TryGetValue(child.ProjectionParticleSource,out int cell))projectionAttachments[child.Id]=(source,cell);
            else if(child.ProjectionParticleSource.StartsWith("cell:",StringComparison.Ordinal)&&int.TryParse(child.ProjectionParticleSource[5..],out int copy))projectionAttachments[child.Id]=(source,copy);
    }
    private bool PrepareProjectionParticles(double time)
    {
        foreach(var source in projectionSources)
        {
            if(source.Prepare is not null&&!source.Prepare(time))return false;
            var frame=source.Frame?.Invoke(time)??DirectorProjectionEvaluator.Evaluate(source.Cells,source.Object.Projection!,time);
            if(!projectionFrames.TryGetValue(source.Object.Id,out var previous)||!ReferenceEquals(previous,frame))
            {projectionFrames[source.Object.Id]=frame;projectionStates[source.Object.Id]=frame.ToDictionary(c=>c.Cell.Id);}
        }
        return true;
    }
    private bool ProjectionEmitterVisible(Guid id)
        =>!projectionAttachments.TryGetValue(id,out var attachment)||projectionStates.TryGetValue(attachment.Source.Object.Id,out var cells)&&cells.TryGetValue(attachment.Cell,out var state)&&state.Visible&&state.Alpha>0;
    private Matrix4x4 ProjectionTransform(Guid id,double time,Func<Guid,double,Matrix4x4> original)
    {
        var raw=original(id,time);
        if(!projectionAttachments.TryGetValue(id,out var attachment)||!projectionStates.TryGetValue(attachment.Source.Object.Id,out var cells)||!cells.TryGetValue(attachment.Cell,out var state))return raw;
        var parent=original(attachment.Source.Object.Id,time);if(!Matrix4x4.Invert(parent,out var inverse))return raw;
        return raw*inverse*Matrix4x4.CreateTranslation(-state.Cell.Position.Float())*state.Matrix*parent;
    }
    private void EmitProjectionParticles(double now)
    {
        foreach(var source in projectionSources)
        {
            var s=source.Object.Projection!;if(s.V("particlesEnabled",now)<.5||s.V("particleRatio",now)<=0)continue;
            string name=s.ParticleScheme.At(now);if(!s.ParticleSchemes.TryGetValue(name,out var effect))continue;
            if(effect.AllNodes().Any(e=>e.Modules is {Geometry:not ParticleGeometry.Particle}))throw new InvalidDataException(source.Object.Name+": block-edge schemes require particle geometry. Attach beam, lightning or trail effects as child objects.");
            int count=(int)s.V("particleCount",now);if(count==0)continue;
            long period=Math.Max(1,(long)Math.Round(s.V("particleInterval",now)/Step));if(tick%period!=0)continue;
            bool lifeOverride=s.V("particleOverride",now)>.5;if(lifeOverride&&s.V("particleLife",now)<=0)continue;
            var frame=projectionStates[source.Object.Id].Values;
            var chosen=frame.Where(c=>c.Visible&&c.Alpha>0&&DirectorProjectionEditing.Random(c.Cell.Id,(int)s.V("seed",now),40)<s.V("particleRatio",now)).ToArray();
            if((long)chosen.Length*count>EffectBudget)throw new CreationParticleBudgetException(source.Object.Name+": structure particle emission exceeds "+EffectBudget);
            var parent=transform(source.Object.Id,now);
            for(int provider=0;provider<effect.Emitters.Count;provider++)
            {
                var original=effect.Emitters[provider];if(!original.Enabled)continue;
                var key=(source.Object.Id,name,provider);
                if(!projectionDefinitions.TryGetValue(key,out var recipe))
                {
                    var e=CreationJson.Clone(original);e.LocalFollow=false;
                    CreationParticleFields.Set(e.Properties,"Quantity",CreationParticleFields.Nat(1));e.Modules?.Drivers.Remove("Quantity");
                    var obj=new CreationSceneObject{Id=source.Object.Id,Name=source.Object.Name,Kind=CreationObjectKind.Particle,ParticleDuration=86400,ParticleEffect=new(){Version=effect.Version,Seed=effect.Seed,Emitters=[e]}};
                    recipe=(obj,e,new(e.Properties,samples,e));projectionDefinitions[key]=recipe;
                }
                foreach(var cell in chosen)for(int i=provider;i<count;i+=Math.Max(1,effect.Emitters.Count))
                {
                    var matrix=cell.Matrix*parent;var edge=DirectorProjectionEditing.Edge(i,count,now*s.V("particleSpeed",now))+recipe.Emitter.Offset;
                    var pos=CreationVector.From(Vector3.Transform(edge.Float(),matrix));int before=live.Count;
                    Spawn(recipe.Object,recipe.Emitter,recipe.Definition,pos,default,now,0,Seed(effect.Seed,tick,unchecked(cell.Cell.Id*131+i)),matrix);
                    if(lifeOverride)for(int n=before;n<live.Count;n++)live[n].Life=Math.Max(Step,s.V("particleLife",now)/20);
                }
            }
        }
    }
    private void StyleProjectionParticles(List<CreationSimulatedParticle> output)
    {
        foreach(var source in projectionSources)
        {
            var s=source.Object.Projection!;double strength=s.V("colorStrength",Time),rainbow=s.V("rainbow",Time),glow=s.V("particleGlow",Time);
            var tint=new CreationVector(s.V("colorR",Time),s.V("colorG",Time),s.V("colorB",Time));var border=new CreationVector(s.V("outlineR",Time),s.V("outlineG",Time),s.V("outlineB",Time));
            int length=output.Count;
            for(int i=0;i<length;i++)
            {
                var p=output[i];if(p.ObjectId!=source.Object.Id)continue;
                var color=p.Color*(1-strength)+tint*strength;
                if(rainbow>0){double hue=((Time*rainbow*.1+DirectorProjectionEditing.Random((int)p.Index,1))%1)*255;color=Hsv(hue,255,255);}
                p=p with{Color=color,Glow=Math.Max(p.Glow,glow)};output[i]=p;
                if(s.V("outline",Time)>.5)output.Add(p with{Index=-p.Index-1,Size=p.Size*1.3,Color=border,Alpha=p.Alpha*.65,Glow=Math.Max(.5,p.Glow)});
            }
        }
    }
}
