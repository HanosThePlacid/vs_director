using System.Diagnostics;
using System.Numerics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VSDirector.Data;

namespace VSDirector.Blueprints;

/// <summary>Incremental construction of batched meshes from the shared structure evaluator.</summary>
internal sealed class DirectorProjectionRenderFrame : IDisposable
{
    private readonly ICoreClientAPI api;
    private readonly BlueprintBuild build;
    private readonly CreationProjectionSettings settings;
    private readonly DirectorProjectionSimulation simulation;
    private IReadOnlyList<ProjectionCellState>? pending;
    private readonly List<BlueprintMeshPart> staging=[];
    internal IReadOnlyList<BlueprintMeshPart> Parts {get;private set;}=[];
    internal IReadOnlyList<ProjectionCellState> Cells {get;private set;}=[];
    private int cursor;
    private double buildingTime=-1;
    internal double Time {get;private set;}=-1;
    internal double Progress=>pending is null?simulation.Progress*.5:.5+.5*cursor/Math.Max(1,pending.Count);
    internal bool Cancelled {get;private set;}
    internal string Error {get;private set;}="";
    internal DirectorProjectionRenderFrame(ICoreClientAPI api,BlueprintBuild build,CreationProjectionSettings settings,ICreationParticleEnvironment? environment=null,System.Func<double,Matrix4x4>? transform=null)
    {this.api=api;this.build=build;this.settings=CreationJson.Clone(settings);simulation=new(build.Cells,settings,environment,transform);}
    internal bool ReadyAt(double time)=>!Cancelled&&Error.Length==0&&Time>=0&&(!settings.Animated||Math.Abs(Time-time)<1e-8);
    internal bool Tick(double time)
    {
        if(Cancelled||build.Cancelled)return false;
        if(ReadyAt(time))return true;
        try
        {
            if(pending is null)
            {
                if(!simulation.Seek(time))return false;
                buildingTime=time;pending=simulation.Frame;cursor=0;
            }
            var watch=Stopwatch.StartNew();
            while(cursor<pending.Count&&(watch.ElapsedMilliseconds<4||cursor==0))
            {
                var state=pending[cursor++];if(!state.Visible||state.Alpha<=0)continue;
                if(settings.ParticleSchemes.ContainsKey(settings.ParticleScheme.At(buildingTime))&&settings.V("particleCount",buildingTime)>0&&settings.V("particlesEnabled",buildingTime)>.5&&settings.V("particleKeepBlocks",buildingTime)<.5&&DirectorProjectionEditing.Random(state.Cell.Id,(int)settings.V("seed",buildingTime),40)<settings.V("particleRatio",buildingTime))continue;
                var mesh=Mesh(state).Clone();mesh.MatrixTransform(Matrix(state.Matrix));
                for(int i=3;i<mesh.Rgba.Length;i+=4)mesh.Rgba[i]=(byte)(mesh.Rgba[i]*state.Alpha);
                var block=api.World.GetBlock(new AssetLocation(state.Code));bool transparent=state.Alpha<.999||block?.RenderPass==EnumChunkRenderPass.Transparent||state.Cell.Liquid;
                var part=staging.LastOrDefault(p=>p.Transparent==transparent&&p.Mesh.VerticesCount+mesh.VerticesCount<32768);
                if(part is null)staging.Add(new(mesh,transparent));else{part.Mesh.AddMeshData(mesh);mesh.Dispose();}
            }
            if(cursor<pending.Count)return false;
            foreach(var part in Parts)part.Dispose();Parts=staging.ToArray();staging.Clear();Cells=pending;pending=null;Time=buildingTime;
            return ReadyAt(time);
        }
        catch(Exception e){Error=e.GetBaseException().Message;throw;}
    }
    private MeshData Mesh(ProjectionCellState state)
    {
        var source=build.Schematic!;var cell=build.Cells[state.Cell.Source];uint packed=source.Indices[cell.Source];
        bool changed=state.Code!=cell.Code;bool special=source.BlockEntities.ContainsKey(packed)||build.Document!.ChiselData.Any(c=>RegionCapture.Pack(c.Position.X,c.Position.Y,c.Position.Z)==packed)||cell.Liquid;
        string key=state.Code+(special&&!changed?"#"+cell.Source:"");
        if(build.CellMeshes.TryGetValue(key,out var cached))return cached;
        var block=api.World.GetBlock(new AssetLocation(state.Code));if(block is null||block.Id==0)throw new InvalidDataException("Missing projection material: "+state.Code);
        MeshData mesh;
        if(!changed&&special)
        {
            var slice=new BlockSchematic{SizeX=source.SizeX,SizeY=source.SizeY,SizeZ=source.SizeZ,Indices=[packed],BlockIds=[source.BlockIds[cell.Source]],BlockCodes=source.BlockCodes,ItemCodes=source.ItemCodes,BlockEntities=source.BlockEntities,OriginalPos=source.OriginalPos};
            if(cell.Liquid)
            {
                var fluids=new BlueprintFluidMesher(api,slice);mesh=new MeshData(4,6);
                while(!fluids.Ready)foreach(var part in fluids.Next()){mesh.AddMeshData(part.Mesh);part.Dispose();}
            }
            else
            {
                var doc=build.Document!;var entities=doc.BlockEntities.Where(e=>RegionCapture.Pack(e.Position.X,e.Position.Y,e.Position.Z)==packed).ToArray();
                mesh=BuildwrightGhostMesh.BuildCpuMesh(api,slice,doc.ChiselData.Where(c=>RegionCapture.Pack(c.Position.X,c.Position.Y,c.Position.Z)==packed),
                    entities.ToDictionary(_=>packed,e=>e.EntityClass??""),entities.ToDictionary(_=>packed,e=>(IReadOnlyList<BuildwrightCompatPayload>)e.CompatData));
            }
            mesh.Translate(-cell.Position.Float().X,-cell.Position.Float().Y,-cell.Position.Float().Z);
        }
        else
        {
            var slice=new BlockSchematic{SizeX=1,SizeY=1,SizeZ=1,Indices=[0],BlockIds=[block.Id],BlockCodes=new(){[block.Id]=block.Code}};
            mesh=BuildwrightGhostMesh.BuildCpuMesh(api,slice);
        }
        if(mesh.VerticesCount==0){mesh.Dispose();throw new InvalidDataException("Projection cell has no mesh: "+state.Code);}
        return build.CellMeshes[key]=mesh;
    }
    internal static float[] Matrix(Matrix4x4 m)=>[m.M11,m.M12,m.M13,m.M14,m.M21,m.M22,m.M23,m.M24,m.M31,m.M32,m.M33,m.M34,m.M41,m.M42,m.M43,m.M44];
    internal void Cancel(){Cancelled=true;simulation.Cancel();}
    public void Dispose(){Cancel();foreach(var p in Parts)p.Dispose();foreach(var p in staging)p.Dispose();staging.Clear();Parts=[];}
}
