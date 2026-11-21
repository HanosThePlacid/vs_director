using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using VSDirector.Data;

namespace VSDirector.Blueprints;

/// <summary>Lossless structural subset/translation. Native files cannot encode display transforms.</summary>
internal static class DirectorProjectionDocument
{
    internal static BlueprintDocument Extract(BlueprintBuild build,CreationProjectionSettings settings,IReadOnlySet<int> selected,string name)
    {
        var cells=DirectorProjectionEditing.Cells(build.Cells,settings).Where(c=>selected.Contains(c.Id)&&settings.Edits.GetValueOrDefault(c.Id)?.Deleted!=true).ToArray();
        if(cells.Length==0)throw new InvalidDataException("The selected region contains no blocks.");
        foreach(var cell in cells)
        {
            var edit=settings.Edits.GetValueOrDefault(cell.Id);var p=DirectorProjectionEditing.Position(cell,settings);
            if(edit is not null&&(edit.Scale!=CreationVector.One||edit.Rotation!=default)||p.X!=Math.Floor(p.X)||p.Y!=Math.Floor(p.Y)||p.Z!=Math.Floor(p.Z))
                throw new InvalidDataException("Native blueprints require integer positions, unit scales and unchanged individual block rotation. Save a Director preset to preserve display transforms.");
        }
        var positions=cells.Select(c=>DirectorProjectionEditing.Position(c,settings)).ToArray();
        var min=new CreationVector(positions.Min(p=>p.X),positions.Min(p=>p.Y),positions.Min(p=>p.Z));
        var size=new Int3((int)(positions.Max(p=>p.X)-min.X+1),(int)(positions.Max(p=>p.Y)-min.Y+1),(int)(positions.Max(p=>p.Z)-min.Z+1));
        if(size.X>1024||size.Y>1024||size.Z>1024)throw new InvalidDataException("Native blueprint dimensions exceed 1024.");
        var doc=JsonConvert.DeserializeObject<BlueprintDocument>(JsonConvert.SerializeObject(build.Document))!;
        doc.Metadata.Name=name;doc.Metadata.Dimensions=size;doc.Metadata.AnchorOffset=new();doc.Metadata.SchematicSha256=null;
        doc.Schematic["SizeX"]=size.X;doc.Schematic["SizeY"]=size.Y;doc.Schematic["SizeZ"]=size.Z;
        doc.Schematic["Indices"]=new JArray();doc.Schematic["BlockIds"]=new JArray();doc.Schematic["BlockEntities"]=new JObject();doc.Schematic["Entities"]=new JArray();
        doc.Schematic["DecorIndices"]=new JArray();doc.Schematic["DecorIds"]=new JArray();doc.BlockEntities=[];doc.ChiselData=[];
        var occupied=new HashSet<(uint,bool)>();
        foreach(var cell in cells)
        {
            var p=DirectorProjectionEditing.Position(cell,settings)-min;var position=new Int3((int)p.X,(int)p.Y,(int)p.Z);uint packed=RegionCapture.Pack(position.X,position.Y,position.Z);
            if(!occupied.Add((packed,cell.Liquid)))throw new InvalidDataException("Overlapping cells cannot be exported to a native blueprint.");
            uint old=build.Schematic!.Indices[cell.Source];
            ((JArray)doc.Schematic["Indices"]!).Add(packed);((JArray)doc.Schematic["BlockIds"]!).Add(build.Schematic.BlockIds[cell.Source]);
            if(!cell.Liquid)
            {
                if(build.Schematic.BlockEntities.TryGetValue(old,out var tree))doc.Schematic["BlockEntities"]![packed.ToString()]=tree;
                foreach(var record in build.Document!.BlockEntities.Where(e=>RegionCapture.Pack(e.Position.X,e.Position.Y,e.Position.Z)==old))
                {var copy=JsonConvert.DeserializeObject<BlockEntityRecord>(JsonConvert.SerializeObject(record))!;copy.Position=position.Copy();doc.BlockEntities.Add(copy);}
                foreach(var record in build.Document.ChiselData.Where(e=>RegionCapture.Pack(e.Position.X,e.Position.Y,e.Position.Z)==old))
                {var copy=JsonConvert.DeserializeObject<ChiselVoxelData>(JsonConvert.SerializeObject(record))!;copy.Position=position.Copy();doc.ChiselData.Add(copy);}
                for(int i=0;i<build.Schematic.DecorIndices.Count;i++)if(build.Schematic.DecorIndices[i]==old)
                {((JArray)doc.Schematic["DecorIndices"]!).Add(packed);((JArray)doc.Schematic["DecorIds"]!).Add(build.Schematic.DecorIds[i]);}
            }
        }
        doc.Metadata.BlockCount=cells.Length;DirectorBlueprintService.Validate(doc);return doc;
    }
}
