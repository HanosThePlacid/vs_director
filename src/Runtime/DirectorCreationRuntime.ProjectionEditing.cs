using System.Numerics;
using VSDirector.Blueprints;
using VSDirector.Data;

namespace VSDirector.Runtime;

internal sealed partial class DirectorCreationRuntime
{
    private long projectionFrameRevision=-1,projectionFrameEnvironment=-1;
    internal bool ProjectionsReady=>Session.Project is not {} project||project.Objects.Where(o=>o.Projection is not null).All(o=>
        Blueprints.Get(project,o) is {Ready:true,Error.Length:0}&&(!o.Projection!.Edited||Renderer.ProjectionFrames.TryGetValue(o.Id,out var f)&&f.ReadyAt(Session.Time)));
    internal double ProjectionProgress=>Renderer.ProjectionFrames?.Values.Select(f=>f.Progress).DefaultIfEmpty(1).Min()??1;
    internal bool ProjectionRebuilding=>Session.Project?.Objects.Any(o=>o.Projection is not null)==true&&Renderer.ProjectionFrames.Values.Any(f=>!f.ReadyAt(Session.Time)&&!f.Cancelled);
    internal string ProjectionError {get;private set;}="";
    internal void ResetProjections()
    {foreach(var frame in Renderer.ProjectionFrames?.Values.AsEnumerable()??[])frame.Dispose();Renderer.ProjectionFrames?.Clear();projectionFrameRevision=-1;ProjectionError="";}
    internal void CancelProjectionRebuild(){foreach(var frame in Renderer.ProjectionFrames.Values)frame.Cancel();Session.Play(false);}
    internal void RebuildProjections(){ResetProjections();InvalidateParticleEnvironment();}
    private void UpdateProjections()
    {
        if(Session.Project is not {} project)return;
        if(projectionFrameRevision!=Session.Revision||projectionFrameEnvironment!=particleEnvironment)
        {ResetProjections();projectionFrameRevision=Session.Revision;projectionFrameEnvironment=particleEnvironment;}
        try
        {
            foreach(var obj in project.Objects.Where(o=>o.Projection is not null))
            {
                var build=Blueprints.Get(project,obj);if(build.Error.Length>0)throw new InvalidDataException(obj.Name+": "+build.Error);
                if(!build.Ready||!obj.Projection!.Edited)continue;
                if(!Renderer.ProjectionFrames.TryGetValue(obj.Id,out var frame))
                    Renderer.ProjectionFrames[obj.Id]=frame=new(api,build,obj.Projection,new ParticleEnvironment(api,project),t=>DirectorCreationEvaluator.Evaluate(project,t).Objects[obj.Id].World);
                try{frame.Tick(Session.Time);}catch(Exception e){throw new InvalidDataException(obj.Name+": "+e.GetBaseException().Message,e);}
                if(Exporting&&!frame.ReadyAt(Session.Time))exportWaitStarted=Environment.TickCount64;
            }
        }
        catch(Exception e){ProjectionError=e.GetBaseException().Message;Status=ProjectionError;Session.Play(false);if(Exporting)CancelExport();}
    }
}
