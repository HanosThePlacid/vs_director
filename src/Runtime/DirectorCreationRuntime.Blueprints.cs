using System.Numerics;
using VSDirector.Blueprints;
using VSDirector.Data;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Runtime;

internal sealed partial class DirectorCreationRuntime
{
    private DirectorBlueprintService? blueprints;
    internal DirectorBlueprintService Blueprints { get { var service=blueprints??=new(api,Session);Renderer.Blueprints=service;return service; } }
    internal CreationSceneObject ImportProjection(BlueprintEntry entry,BlueprintBuild build,CreationVector position)
    {
        if(!build.Ready||build.Document is not {} document)throw new InvalidOperationException(L("projection-loading"));
        var obj=new CreationSceneObject{Kind=CreationObjectKind.Structure,Name=document.Metadata.Name,Projection=new(){Version=2,
            Size=new(document.Metadata.Dimensions.X,document.Metadata.Dimensions.Y,document.Metadata.Dimensions.Z),
            Anchor=new(document.Metadata.AnchorOffset.X,document.Metadata.AnchorOffset.Y,document.Metadata.AnchorOffset.Z),Author=document.Metadata.Author},
            Transform=new(){Position=position-new CreationVector(document.Metadata.AnchorOffset.X,document.Metadata.AnchorOffset.Y,document.Metadata.AnchorOffset.Z), Pivot=new(document.Metadata.Dimensions.X*.5,0,document.Metadata.Dimensions.Z*.5)}};
        string? temp=null;
        try
        {
            string source=entry.Source;
            if(entry.Asset){temp=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".json");File.WriteAllBytes(temp,api.Assets.Get(new AssetLocation(entry.Source)).Data);source=temp;}
            Session.Edit(L("projection-import"),p=>
            {
                obj.AssetId=Session.Store.Import(p,source,CreationAssetKind.Structure).Id;
                p.Objects.Add(obj);AddBlueprintParticles(p,obj,build.Schematic!);
            });
            return obj;
        }
        finally{if(temp is not null)File.Delete(temp);}
    }
    private void AddBlueprintParticles(DirectorCreationProject project,CreationSceneObject parent,BlockSchematic schematic)
    {
        var seen=new HashSet<uint>();
        for(int i=0;i<schematic.Indices.Count;i++)
        {
            uint packed=schematic.Indices[i];var code=schematic.BlockCodes[schematic.BlockIds[i]];
            bool supported=code.Domain=="game"&&(code.Path.StartsWith("torch-")&&code.Path.Contains("-lit-")||code.Path is "fire" or "firepit-lit" or "firepit-extinct");
            if(!supported||!seen.Add(packed)||api.World.GetBlock(code) is not {} block||block.ParticleProperties is not {Length:>0} providers)continue;
            var resources=providers.Select((p,index)=>new DirectorParticleResource(code+"/"+index,code.ToString(),code.ToString(),code.ToString(),"block",index,DirectorParticleNativeAdapter.Read(p),block)).ToArray();
            var effect=new CreationParticleEffect{Name=code.ToString(),Emitters=resources.Select(r=>r.Copy()).ToList()};
            DirectorParticlePreparation.Prepare(effect,resources,false);effect.Upgrade();
            foreach(var emitter in effect.Emitters){emitter.Duration=86400;emitter.Interval=.05;}
            var local=RegionCapture.Unpack(packed);
            project.Objects.Add(new(){Name=code.ToString(),Kind=CreationObjectKind.Particle,ParentId=parent.Id,ProjectionParticleSource=packed+":"+code,
                Transform=new(){Position=new(local.X+.5,local.Y,local.Z+.5)},ParticleDuration=86400,ParticleEffect=effect});
        }
    }
    internal void FocusProjection(CreationSceneObject obj)
    {
        if(obj.Projection is not {} projection||Session.Project is not {} project||Session.Frame?.Objects.GetValueOrDefault(obj.Id) is not {} state)return;
        var center=Vector3.Transform(projection.Size.Float()*.5f,state.World);
        owner.FocusCreationView(project.Origin+CreationVector.From(center),Math.Max(.5,projection.Size.Float().Length()*state.Transform.Scale.Float().Length()/3),project.Dimension);
    }
    internal BlueprintDocument CaptureBlueprint(CreationVector first,CreationVector second)
    {
        if(Session.Project is not {} project)throw new InvalidOperationException(L("creation-open-a-project-first"));
        var min=new BlockPos((int)Math.Min(first.X,second.X),(int)Math.Min(first.Y,second.Y),(int)Math.Min(first.Z,second.Z));
        var max=new BlockPos((int)Math.Max(first.X,second.X),(int)Math.Max(first.Y,second.Y),(int)Math.Max(first.Z,second.Z));
        long volume=(long)(max.X-min.X+1)*(max.Y-min.Y+1)*(max.Z-min.Z+1);
        if(volume>1000000)throw new InvalidDataException(L("projection-too-large"));
        if(volume<=0||!first.Finite()||!second.Finite())throw new InvalidDataException(L("projection-too-large"));
        int ChunkStart(int value)=>(int)Math.Floor(value/32d)*32;
        for(int x=ChunkStart(min.X);x<=max.X;x+=32)for(int y=ChunkStart(min.Y);y<=max.Y;y+=32)for(int z=ChunkStart(min.Z);z<=max.Z;z+=32)
            if(api.World.BlockAccessor.GetChunkAtBlockPos(new BlockPos(x,y,z)) is null)throw new InvalidOperationException(L("projection-unloaded"));
        var result=RegionCapture.Capture(api.World,min,max,min,null);DirectorBlueprintService.Validate(result);return result;
    }
}
