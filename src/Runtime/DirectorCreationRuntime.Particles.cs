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
    internal Guid ParticleSolo {get;set;}
    private bool particleWorldWasReady;
    private readonly Dictionary<double,CreationFrame> particleTransforms=new();
    internal bool ParticlesReady => Session.Project is not {} project || !project.Objects.Any(o=>o.Kind==CreationObjectKind.Particle&&o.ParticleEffect is not null||o.Projection is {ParticleSchemes.Count:>0})
        || particleSimulation is {} simulation && simulation.Ready && particleProject==project.Id && particleRevision==Session.Revision
        && particleBuiltEnvironment==particleEnvironment && Math.Abs(simulation.Time-Math.Floor(Session.Time/DirectorCreationParticleSimulation.Step+1e-7)*DirectorCreationParticleSimulation.Step)<1e-7;
    internal double ParticleProgress => particleSimulation?.Progress??1;
    internal string ParticleError => particleSimulation?.Error??"";
    internal bool ParticleRebuildCancelled {get;private set;}
    internal void InvalidateParticleEnvironment(){particleEnvironment++;ParticleRebuildCancelled=false;}
    private void ParticleBlockChanged(BlockPos pos,Block oldBlock)=>InvalidateParticleEnvironment();
    internal void CancelParticleRebuild(){particleSimulation?.Cancel();ParticleRebuildCancelled=true;Session.Play(false);}
    internal void ResetParticles(){particleSimulation=null;particleTransforms?.Clear();ParticleOverrides?.Restore();if(Renderer is not null){Renderer.AdvancedParticles=[];Renderer.ParticleRibbons=[];}particleProject=Guid.Empty;ParticleRebuildCancelled=false;particleRevision=-1;}
    private void UpdateParticles()
    {
        particleResources?.Tick();
        if(Session.Project is not {} project)return;
        bool worldReady=CreationWorldReady&&(project.BackgroundReplayId==Guid.Empty||project.BackgroundReplayId==owner.ActiveReplayId&&owner.CreationBackgroundReady);
        if(!worldReady||!ProjectionsReady){particleWorldWasReady=false;Renderer.AdvancedParticles=[];Renderer.ParticleRibbons=[];return;}
        if(!particleWorldWasReady){particleWorldWasReady=true;InvalidateParticleEnvironment();}
        if(particleProject!=project.Id){ResetParticles();particleProject=project.Id;}
        if(particleRevision!=Session.Revision)
        {ParticleOverrides.Refresh(project);if(ParticleOverrides.Notice.Length>0)Status=ParticleOverrides.Notice;}
        if(!project.Objects.Any(o=>o.Kind==CreationObjectKind.Particle&&o.ParticleEffect is not null||o.Projection is {ParticleSchemes.Count:>0}))
        {particleSimulation=null;Renderer.AdvancedParticles=[];Renderer.ParticleRibbons=[];particleRevision=Session.Revision;return;}
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
            foreach(var obj in project.Objects.Where(o=>o.Projection is not null))
            {
                var build=Blueprints.Get(project,obj);var physical=new DirectorProjectionSimulation(build.Cells,obj.Projection!,new ParticleEnvironment(api,project),t=>DirectorCreationEvaluator.Evaluate(project,t).Objects[obj.Id].World);
                particleSimulation.AddProjection(new(obj,build.Cells,t=>physical.Frame,physical.Seek));
            }
            particleSimulation.Attachment=(id,bone,time)=>{var frame=DirectorCreationEvaluator.Evaluate(project,time);PrepareRuntimeFrame(project,frame,false);return frame.Objects.TryGetValue(id,out var state)?(bone!=Guid.Empty&&state.Bones.TryGetValue(bone,out var b)?b*state.World:state.World):Matrix4x4.Identity;};
        }
        try
        {
            double previous=particleSimulation.Time;
            bool ready=particleSimulation.Seek(Session.Time,240);
            if(Exporting&&particleSimulation.Time!=previous)exportWaitStarted=Environment.TickCount64;
            Renderer.AdvancedParticles=ready?particleSimulation.Particles.Where(p=>Exporting||ParticleSolo==Guid.Empty||p.EmitterId==ParticleSolo).ToArray():[];
            Renderer.ParticleRibbons=ready?particleSimulation.Ribbons.Where(p=>Exporting||ParticleSolo==Guid.Empty||p.EmitterId==ParticleSolo).ToArray():[];
        }
        catch(Exception e) when(e is CreationParticleBudgetException or InvalidDataException or ArithmeticException)
        {
            Renderer.AdvancedParticles=[];Renderer.ParticleRibbons=[];Session.Play(false);Status=e.Message;
            if(Exporting)CancelExport();Status=e.Message;
        }
    }
    private sealed class ParticleEnvironment(ICoreClientAPI api,DirectorCreationProject project) : ICreationParticleEnvironment
    {
        public CreationVector Wind { get; } = new(GlobalConstants.CurrentWindSpeedClient.X,GlobalConstants.CurrentWindSpeedClient.Y,GlobalConstants.CurrentWindSpeedClient.Z);
        private readonly ClimateCondition? climate=api.World.BlockAccessor.GetClimateAt(new BlockPos((int)project.Origin.X,(int)project.Origin.Y,(int)project.Origin.Z,project.Dimension));
        public double Temperature=>climate?.Temperature??20;
        public double Rain=>climate?.Rainfall??0;
        private readonly Dictionary<string,CreationVector> colors=new();
        public CreationVector BlockColor(string code)
        {
            if(colors.TryGetValue(code,out var cached))return cached;
            var block=api.World.GetBlock(new AssetLocation(code))??throw new InvalidDataException("Unknown color block: "+code);
            int color=block.GetRandomColor(api,new BlockPos((int)project.Origin.X,(int)project.Origin.Y,(int)project.Origin.Z,project.Dimension),BlockFacing.UP,0);
            return colors[code]=new((color&255)/255d,((color>>8)&255)/255d,((color>>16)&255)/255d);
        }
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
                new((color&255)/255d,((color>>8)&255)/255d,((color>>16)&255)/255d),boxes?.Select(b=>new CreationParticleBox(new(b.X1,b.Y1,b.Z1),new(b.X2,b.Y2,b.Z2))).ToArray(),block.Code?.ToString()??"");
        }
    }
}
