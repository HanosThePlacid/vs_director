using System.Diagnostics;
using Newtonsoft.Json.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using VSDirector.Data;

namespace VSDirector.Blueprints;

internal sealed record BlueprintEntry(string Name,string Source,string Category,bool Asset=false);
internal sealed class BlueprintMeshPart(MeshData mesh,bool transparent) : IDisposable
{
    internal MeshData Mesh { get; } = mesh;
    internal bool Transparent { get; } = transparent;
    internal MultiTextureMeshRef? Gpu;
    public void Dispose(){Gpu?.Dispose();Mesh.Dispose();}
}
internal sealed class BlueprintBuild : IDisposable
{
    internal readonly Task<BlueprintDocument> Read;
    internal BlueprintDocument? Document;
    internal BlockSchematic? Schematic;
    internal BlueprintFluidMesher? Fluids;
    internal IReadOnlyList<ProjectionCell> Cells=[];
    private IReadOnlyList<ProjectionCellState>? baseFrame;
    internal IReadOnlyList<ProjectionCellState> BaseFrame=>baseFrame??=Cells.Select(c=>new ProjectionCellState(c,System.Numerics.Matrix4x4.CreateTranslation(c.Position.Float()),c.Code,true,1,1)).ToArray();
    internal readonly Dictionary<string,MeshData> CellMeshes=[];
    internal readonly List<BlueprintMeshPart> Parts=[];
    internal readonly List<string> Issues=[];
    internal string Error="";
    internal bool Ready,Cancelled;
    internal int Cursor;
    internal double Progress=>Ready?1:Schematic is null?0:(double)Cursor/Math.Max(1,Schematic.Indices.Count);
    internal BlueprintBuild(Func<BlueprintDocument> read)=>Read=Task.Run(read);
    public void Dispose(){Cancelled=true;foreach(var p in Parts)p.Dispose();Parts.Clear();foreach(var mesh in CellMeshes.Values)mesh.Dispose();CellMeshes.Clear();}
}

/// <summary>One owned mesh cache shared by the scene, browser and image exporter. Game APIs stay on the main thread.</summary>
internal sealed class DirectorBlueprintService(ICoreClientAPI api,DirectorCreationSession session) : IDisposable
{
    internal string Library=>Path.Combine(GamePaths.DataPath,"Buildwright","Blueprints");
    internal List<BlueprintEntry> Entries { get; private set; }=[];
    internal IReadOnlyList<string> PresetFiles {get;private set;}=[];
    private Task<List<BlueprintEntry>>? scan;
    private readonly Dictionary<string,BlueprintBuild> builds=new(StringComparer.OrdinalIgnoreCase);
    private Guid projectId;
    internal bool Scanning=>scan is not null;
    internal string ScanError="";
    internal void Scan()
    {
        if(scan is not null)return;
        var resources=api.Assets.AllAssets.Keys.Where(k=>k.Path.EndsWith(".json",StringComparison.OrdinalIgnoreCase)
            &&(k.Path.StartsWith("worldgen/schematics/",StringComparison.Ordinal)||k.Path.StartsWith("schematics/",StringComparison.Ordinal)))
            .Select(k=>new BlueprintEntry(k.Path,k.ToString(),k.Domain=="game"?"vanilla":"mods/"+k.Domain,true)).ToList();
        scan=Task.Run(()=>
        {
            if(Directory.Exists(Library))foreach(var file in Directory.EnumerateFiles(Library,"*",SearchOption.AllDirectories))
                if(Path.GetExtension(file).ToLowerInvariant() is ".vsbp" or ".json")resources.Add(new(Path.GetFileNameWithoutExtension(file),file,"local/"+(Path.GetDirectoryName(Path.GetRelativePath(Library,file))??"")));
            return resources.OrderBy(r=>r.Category).ThenBy(r=>r.Name).ToList();
        });
    }
    internal BlueprintBuild Get(BlueprintEntry entry)
    {
        if(builds.TryGetValue(entry.Source,out var existing))return existing;
        // Asset manager access is deliberately outside the worker task.
        string? text=entry.Asset?api.Assets.Get(new AssetLocation(entry.Source)).ToText():null;
        var build=new BlueprintBuild(()=>
        {
            var document=text is null?new BlueprintCodec().Read(entry.Source):Parse(text,entry.Name);
            Validate(document);return document;
        });
        builds.Add(entry.Source,build);return build;
    }
    internal BlueprintBuild Get(DirectorCreationProject project,CreationSceneObject obj)
    {
        var asset=project.Assets.FirstOrDefault(a=>a.Id==obj.AssetId)??throw new InvalidDataException("Missing blueprint asset: "+obj.Name);
        return Get(new(asset.Name,session.Store.AssetPath(project.Id,asset.Source),"project"));
    }
    internal void Tick()
    {
        if(projectId!=(session.Project?.Id??Guid.Empty)){Clear();projectId=session.Project?.Id??Guid.Empty;}
        if(scan?.IsCompleted==true){try{var found=scan.GetAwaiter().GetResult();PresetFiles=found.Where(e=>e.Source.EndsWith(".director-structure.json",StringComparison.OrdinalIgnoreCase)).Select(e=>e.Source).ToArray();Entries=found.Where(e=>!e.Source.EndsWith(".director-structure.json",StringComparison.OrdinalIgnoreCase)).ToList();ScanError="";}catch(Exception e){ScanError=e.Message;}scan=null;}
        var watch=Stopwatch.StartNew();
        foreach(var build in builds.Values.ToArray())
        {
            if(build.Ready||build.Cancelled||build.Error.Length>0||!build.Read.IsCompleted)continue;
            try
            {
                if(build.Document is null)
                {
                    build.Document=build.Read.GetAwaiter().GetResult();
                    build.Schematic=JsonUtil.FromString<BlockSchematic>(build.Document.Schematic.ToString())!;
                    foreach(var be in build.Document.BlockEntities)build.Schematic.BlockEntities[RegionCapture.Pack(be.Position.X,be.Position.Y,be.Position.Z)]=be.TreeAscii85;
                    if((build.Document.Schematic["Entities"] as JArray)?.Count>0)build.Issues.Add("Blueprint entities are retained in the file but are not spawned.");
                    if(build.Schematic.DecorIndices.Count>0)build.Issues.Add("Surface decorations are retained in the file but are not rendered.");
                    build.Cells=build.Schematic.Indices.Select((packed,index)=>{var p=RegionCapture.Unpack(packed);var code=build.Schematic.BlockCodes[build.Schematic.BlockIds[index]];return new ProjectionCell(index,index,new(p.X,p.Y,p.Z),code.ToString(),api.World.GetBlock(code)?.ForFluidsLayer==true);}).ToArray();
                    build.Fluids=new(api,build.Schematic);
                }
                BuildPart(build);
            }
            catch(Exception e){build.Error=e.GetBaseException().Message;}
            if(watch.ElapsedMilliseconds>=4)break;
        }
    }
    private void BuildPart(BlueprintBuild build)
    {
        var source=build.Schematic!;var document=build.Document!;
        int end=Math.Min(source.Indices.Count,build.Cursor+128);
        // Build in bounded slices so even large schematics do not stall the UI.
        foreach(bool fluid in new[]{false,true})
        {
            var slice=new BlockSchematic{SizeX=source.SizeX,SizeY=source.SizeY,SizeZ=source.SizeZ,BlockCodes=source.BlockCodes,ItemCodes=source.ItemCodes,
                BlockEntities=source.BlockEntities,OriginalPos=source.OriginalPos};
            for(int i=build.Cursor;i<end;i++)
            {
                uint packed=source.Indices[i];
                var code=source.BlockCodes[source.BlockIds[i]];
                var block=api.World.GetBlock(code);
                if(block is null||block.Id==0&&code.Path!="air"){if(!fluid)build.Issues.Add(code+" @ "+RegionCapture.Unpack(packed));continue;}
                if(block.Id==0||block.ForFluidsLayer||block.DrawType==EnumDrawType.Liquid)continue;
                bool transparent=block.RenderPass is EnumChunkRenderPass.Transparent;
                if(transparent!=fluid)continue;
                slice.Indices.Add(packed);slice.BlockIds.Add(source.BlockIds[i]);
            }
            if(slice.Indices.Count==0)continue;
            try
            {
                var entities=document.BlockEntities.GroupBy(e=>RegionCapture.Pack(e.Position.X,e.Position.Y,e.Position.Z)).ToDictionary(g=>g.Key,g=>g.Last().EntityClass??"");
                var compat=document.BlockEntities.GroupBy(e=>RegionCapture.Pack(e.Position.X,e.Position.Y,e.Position.Z)).ToDictionary(g=>g.Key,g=>(IReadOnlyList<BuildwrightCompatPayload>)g.Last().CompatData);
                var mesh=BuildwrightGhostMesh.BuildCpuMesh(api,slice,document.ChiselData,entities,compat);
                if(mesh.VerticesCount>0)build.Parts.Add(new(mesh,fluid));else mesh.Dispose();
            }
            catch(Exception e){build.Issues.Add($"Cells {build.Cursor}–{end}: {e.GetBaseException().Message}");}
        }
        build.Cursor=end;
        if(end==source.Indices.Count && build.Fluids is {Ready:false} fluids)build.Parts.AddRange(fluids.Next());
        build.Ready=end==source.Indices.Count && build.Fluids?.Ready!=false;
    }
    internal static BlueprintDocument Parse(string text,string name)
    {
        var root=JObject.Parse(text);var schematic=root["schematic"] as JObject??root;
        return new(){Schematic=schematic,Metadata=new(){Name=name,Dimensions=new(schematic["SizeX"]?.Value<int>()??0,schematic["SizeY"]?.Value<int>()??0,schematic["SizeZ"]?.Value<int>()??0),AnchorOffset=root["anchorOffset"]?.ToObject<Int3>()??new()}};
    }
    internal static void Validate(BlueprintDocument d)
    {
        var s=d.Schematic;int x=s["SizeX"]?.Value<int>()??0,y=s["SizeY"]?.Value<int>()??0,z=s["SizeZ"]?.Value<int>()??0;
        if(x is <1 or >1024||y is <1 or >1024||z is <1 or >1024||s["Indices"] is not JArray indices||s["BlockIds"] is not JArray ids||s["BlockCodes"] is not JObject codes||indices.Count!=ids.Count||indices.Count==0||indices.Count>1000000)
            throw new InvalidDataException("Invalid/empty block schematic (not a Shape model); maximum 1,000,000 cells, 1024 per axis.");
        for(int i=0;i<indices.Count;i++)
        {
            uint p=indices[i].Value<uint>();var c=RegionCapture.Unpack(p);
            if(p>>30!=0||c.X>=x||c.Y>=y||c.Z>=z||codes[ids[i].ToString()]?.Type!=JTokenType.String)throw new InvalidDataException("Invalid schematic cell or block mapping: "+i);
        }
        d.Metadata.Dimensions=new(x,y,z);d.Metadata.BlockCount=indices.Count;
    }
    internal void Clear(){foreach(var b in builds.Values)b.Dispose();builds.Clear();BlueprintFluidMesher.Release();}
    internal void Retry(BlueprintEntry entry){if(builds.Remove(entry.Source,out var old))old.Dispose();}
    public void Dispose()=>Clear();
}
