using System.Numerics;
using VSDirector.Data;
using VSDirector.Rendering;
using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace VSDirector.Blueprints;

internal sealed class DirectorProjectionScene : IDisposable
{
    internal readonly DirectorCreationProject Project;
    internal readonly Guid ObjectId;
    internal readonly BlueprintBuild Build;
    internal readonly DirectorPickerMeshPreview Renderer=new();
    internal readonly CreationProjectionView View;
    private readonly DirectorCreationParticleSimulation simulation;
    private readonly System.Func<string,int> texture;
    private Matrix4x4 builtMatrix;
    private bool meshed;
    private readonly DirectorProjectionRenderFrame? edited;
    private double editedTime=-1;
    internal double Progress=>edited is not null&&!edited.ReadyAt(CurrentTime)?edited.Progress:simulation.Progress;
    internal double CurrentTime {get;private set;}
    internal Matrix4x4 CurrentWorld {get;private set;}=Matrix4x4.Identity;
    internal IReadOnlyList<ProjectionCellState> CurrentCells=>edited?.Cells??Build.BaseFrame;
    internal DirectorProjectionScene(ICoreClientAPI api,DirectorCreationProject source,Guid id,BlueprintBuild build,System.Func<string,int> texture)
    {
        ObjectId=id;Build=build;this.texture=texture;Project=source.Clone();
        var ids=new HashSet<Guid>{id};bool changed;
        do{changed=false;foreach(var obj in Project.Objects)if(ids.Contains(obj.ParentId))changed|=ids.Add(obj.Id);}while(changed);
        Project.Objects.RemoveAll(o=>!ids.Contains(o.Id));
        Project.Tracks.RemoveAll(t=>!ids.Contains(t.ObjectId));Project.Clips.RemoveAll(c=>!ids.Contains(c.ObjectId));
        var root=Project.Objects.First(o=>o.Id==id);
        // Keep the original ancestor transforms when evaluating the isolated subtree.
        var ancestor=root.ParentId;
        while(ancestor!=Guid.Empty && source.Objects.Find(o=>o.Id==ancestor) is {} parent)
        {
            Project.Objects.Add(CreationJson.Clone(parent));
            Project.Tracks.AddRange(source.Tracks.Where(t=>t.ObjectId==ancestor).Select(CreationJson.Clone));
            Project.Clips.AddRange(source.Clips.Where(c=>c.ObjectId==ancestor).Select(CreationJson.Clone));
            ancestor=parent.ParentId;
        }
        View=CreationJson.Clone(root.Projection!.View);Renderer.ProjectionView=View;
        // Remove only the placement translation. Rotation/scale and authored animation remain visible.
        var origin=DirectorCreationEvaluator.Evaluate(Project,View.Start).Objects[id].World.Translation;
        Matrix4x4 Transform(Guid obj,double t)=>DirectorCreationEvaluator.Evaluate(Project,View.Animate?t:View.Start).Objects[obj].World*Matrix4x4.CreateTranslation(-origin);
        Renderer.SetProjection(build.Parts,Transform(id,View.Start));Renderer.LockProjectionBounds=true;
        simulation=new(Project,Transform,new Environment(api,build,Transform(id,View.Start)));
        if(root.Projection!.Edited)edited=new(api,build,root.Projection,new Environment(api,build,Transform(id,View.Start)),t=>Transform(id,t));
        if(root.Projection is not null)
        {
            var physical=new DirectorProjectionSimulation(build.Cells,root.Projection,new Environment(api,build,Transform(id,View.Start)),t=>Transform(id,t));
            simulation.AddProjection(new(root,build.Cells,t=>physical.Frame,physical.Seek));
        }
        transform=(t)=>Transform(id,t);
    }
    private readonly System.Func<double,Matrix4x4> transform;
    internal bool Render(double elapsed,int width,int height)
    {
        if(Build.Cancelled)throw new InvalidOperationException("Blueprint cache was invalidated. Reopen the preview.");
        if(!Build.Ready) return false;
        double time=View.Start+elapsed;
        if(edited is not null&&!edited.Tick(View.Animate?time:View.Start))return false;
        if(View.Particles&&!simulation.Seek(time,600)){if(simulation.Error.Length>0)throw new InvalidOperationException(simulation.Error);return false;}
        var matrix=transform(time);CurrentWorld=matrix;CurrentTime=View.Animate?time:View.Start;
        if(!meshed||matrix!=builtMatrix||edited is not null&&editedTime!=edited.Time){Renderer.SetProjection(edited?.Parts??Build.Parts,matrix);builtMatrix=matrix;meshed=true;editedTime=edited?.Time??-1;}
        Renderer.SetProjectionParticles(View.Particles?simulation.Particles:[],elapsed,texture,View.Particles?simulation.Ribbons:[]);
        Renderer.SetSize(width,height);Renderer.Render(elapsed,width/(float)height);return true;
    }
    public void Dispose(){edited?.Dispose();simulation.Cancel();Renderer.Dispose();}
    private sealed class Environment : ICreationParticleEnvironment
    {
        private readonly Dictionary<(int,int,int),CreationParticleCell> cells=[];
        public CreationVector Wind=>default;
        internal Environment(ICoreClientAPI api,BlueprintBuild build,Matrix4x4 matrix)
        {
            var s=build.Schematic!;
            for(int i=0;i<s.Indices.Count;i++)
            {
                var local=RegionCapture.Unpack(s.Indices[i]);var p=Vector3.Transform(new(local.X+.5f,local.Y+.5f,local.Z+.5f),matrix);
                var block=api.World.GetBlock(s.BlockCodes[s.BlockIds[i]]);if(block is null||block.Id==0)continue;
                var key=((int)Math.Floor(p.X),(int)Math.Floor(p.Y),(int)Math.Floor(p.Z));bool liquid=block.ForFluidsLayer;
                var previous=cells.GetValueOrDefault(key);
                cells[key]=new(previous.Solid||(!liquid&&block.CollisionBoxes is {Length:>0}),previous.Liquid||liquid,key.Item2+1,CreationVector.One);
            }
        }
        public CreationParticleCell Cell(int x,int y,int z)=>cells.GetValueOrDefault((x,y,z),new(false,false,double.NegativeInfinity,CreationVector.One));
    }
}
