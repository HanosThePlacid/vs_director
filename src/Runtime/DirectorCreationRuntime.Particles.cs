using System.Numerics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using VSDirector.Data;

namespace VSDirector.Runtime;

internal sealed partial class DirectorCreationRuntime
{
    private DirectorCreationParticleResources? particleResources;
    internal DirectorCreationParticleResources ParticleResources => particleResources ??= new(api);
    internal DirectorCreationParticleOverrides ParticleOverrides { get; } = new();
    private DirectorCreationParticleSimulation? particleSimulation;
    private long particleRevision=-1,particleEnvironment,particleBuiltEnvironment;
    private Guid particleProject;
    private bool particleWorldWasReady;
    private readonly Dictionary<double,CreationFrame> particleTransforms=new();
    internal bool ParticlesReady => Session.Project is not {} project || !project.Objects.Any(o=>o.Kind==CreationObjectKind.Particle&&o.ParticleEffect is not null)
        || particleSimulation is {} simulation && simulation.Ready && particleProject==project.Id && particleRevision==Session.Revision
        && particleBuiltEnvironment==particleEnvironment && Math.Abs(simulation.Time-Math.Floor(Session.Time/DirectorCreationParticleSimulation.Step+1e-7)*DirectorCreationParticleSimulation.Step)<1e-7;
    internal double ParticleProgress => particleSimulation?.Progress??1;
    internal string ParticleError => particleSimulation?.Error??"";
    internal bool ParticleRebuildCancelled {get;private set;}
    internal void InvalidateParticleEnvironment(){particleEnvironment++;ParticleRebuildCancelled=false;}
    private void ParticleBlockChanged(BlockPos pos,Block oldBlock)=>InvalidateParticleEnvironment();
    internal void CancelParticleRebuild(){particleSimulation?.Cancel();ParticleRebuildCancelled=true;Session.Play(false);}
    internal void ResetParticles(){particleSimulation=null;particleTransforms?.Clear();ParticleOverrides?.Restore();if(Renderer is not null)Renderer.AdvancedParticles=[];particleProject=Guid.Empty;ParticleRebuildCancelled=false;particleRevision=-1;}
    private void UpdateParticles()
    {
        particleResources?.Tick();
        if(Session.Project is not {} project)return;
        bool worldReady=CreationWorldReady&&(project.BackgroundReplayId==Guid.Empty||project.BackgroundReplayId==owner.ActiveReplayId&&owner.CreationBackgroundReady);
        if(!worldReady){particleWorldWasReady=false;Renderer.AdvancedParticles=[];return;}
        if(!particleWorldWasReady){particleWorldWasReady=true;InvalidateParticleEnvironment();}
        if(particleProject!=project.Id){ResetParticles();particleProject=project.Id;}
        if(particleRevision!=Session.Revision)
        {ParticleOverrides.Refresh(project);if(ParticleOverrides.Notice.Length>0)Status=ParticleOverrides.Notice;}
        if(!project.Objects.Any(o=>o.Kind==CreationObjectKind.Particle&&o.ParticleEffect is not null))
        {particleSimulation=null;Renderer.AdvancedParticles=[];particleRevision=Session.Revision;return;}
        if(particleRevision!=Session.Revision || particleBuiltEnvironment!=particleEnvironment || particleSimulation is null)
        {
            particleRevision=Session.Revision;particleBuiltEnvironment=particleEnvironment;particleTransforms.Clear();ParticleRebuildCancelled=false;
            particleSimulation=new(project,(id,time)=>
            {
                if(!particleTransforms.TryGetValue(time,out var frame))
                {
                    frame=DirectorCreationEvaluator.Evaluate(project,time);PrepareRuntimeFrame(project,frame,false);particleTransforms[time]=frame;
                    if(particleTransforms.Count>1024)particleTransforms.Remove(particleTransforms.Keys.First());
                }
                return frame.Objects.TryGetValue(id,out var state)?state.World:Matrix4x4.Identity;
            },new ParticleEnvironment(api,project));
        }
        try
        {
            double previous=particleSimulation.Time;
            bool ready=particleSimulation.Seek(Session.Time,240);
            if(Exporting&&particleSimulation.Time!=previous)exportWaitStarted=Environment.TickCount64;
            Renderer.AdvancedParticles=ready?particleSimulation.Particles:[];
        }
        catch(CreationParticleBudgetException e)
        {
            Renderer.AdvancedParticles=[];Session.Play(false);Status=e.Message;
            if(Exporting)CancelExport();Status=e.Message;
        }
    }
    private sealed class ParticleEnvironment(ICoreClientAPI api,DirectorCreationProject project) : ICreationParticleEnvironment
    {
        public CreationVector Wind { get; } = new(GlobalConstants.CurrentWindSpeedClient.X,GlobalConstants.CurrentWindSpeedClient.Y,GlobalConstants.CurrentWindSpeedClient.Z);
        public CreationVector GridOrigin => project.Origin;
        public CreationParticleCell Cell(int x,int y,int z)
        {
            var position=new BlockPos(x,y,z,project.Dimension);
            var accessor=api.World.BlockAccessor;
            var block=accessor.GetBlock(position,BlockLayersAccess.Solid);
            var liquid=accessor.GetBlock(position,BlockLayersAccess.Fluid);
            var boxes=block.GetCollisionBoxes(accessor,position);
            bool solid=boxes?.Length>0;
            int color=block.Id==0?unchecked((int)0xffffffff):block.GetRandomColor(api,position,BlockFacing.UP,0);
            return new(solid,liquid.IsLiquid(),accessor.GetRainMapHeightAt(position)-project.Origin.Y,
                new((color&255)/255d,((color>>8)&255)/255d,((color>>16)&255)/255d),boxes?.Select(b=>new CreationParticleBox(new(b.X1,b.Y1,b.Z1),new(b.X2,b.Y2,b.Z2))).ToArray());
        }
    }
}
