using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using VSDirector.Blueprints;
using VSDirector.Data;
using Vintagestory.API.Common;

internal static class DirectorBlueprintChecks
{
    internal static void Run()
    {
        static void Check(bool value,string message){if(!value)throw new InvalidOperationException(message);}
        string root=Path.Combine(Path.GetTempPath(),"vsdirector-blueprint-probe-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var document=new BlueprintDocument
            {
                Metadata=new(){Name="Probe",Author="Probe author",Dimensions=new(2,1,1),AnchorOffset=new(1,0,0)},
                Schematic=JObject.Parse("""
                    {"SizeX":2,"SizeY":1,"SizeZ":1,"Indices":[0,1],"BlockIds":[1,2],"BlockCodes":{"1":"game:stone","2":"game:torch-lit-up"},"BlockEntities":{"1":"payload"}}
                    """),
                BlockEntities=[new(){Position=new(1,0,0),EntityClass="ProbeEntity",TreeAscii85="payload",CompatData=[new(){AdapterId="probe",Data=new JObject{["value"]=7}}]}],
                ChiselData=[new(){Position=new(0,0,0),BlockIds=[1],VoxelCuboids=[123],OriginalVoxelCuboids=[123]}]
            };
            var codec=new BlueprintCodec();string archive=Path.Combine(root,"probe.vsbp");codec.Write(archive,document);
            var roundtrip=codec.Read(archive);
            Check(roundtrip.Metadata.Name=="Probe"&&roundtrip.Metadata.Author=="Probe author","Buildwright blueprint metadata was lost");
            Check(roundtrip.Schematic["Indices"]!.Count()==2&&roundtrip.BlockEntities.Single().CompatData.Single().Data["value"]!.Value<int>()==7,"Blueprint block or compatibility payload was lost");
            Check(roundtrip.ChiselData.Single().VoxelCuboids.SequenceEqual([123u]),"Blueprint chisel data was lost");

            var assembly=typeof(BlueprintCodec).Assembly;
            var buildType=assembly.GetType("VSDirector.Blueprints.BlueprintBuild",true)!;
            var build=Activator.CreateInstance(buildType,BindingFlags.Instance|BindingFlags.NonPublic,null,[new Func<BlueprintDocument>(()=>document)],null)!;
            AccessTools.Field(buildType,"Document").SetValue(build,document);
            var schematic=JsonUtil.FromString<BlockSchematic>(document.Schematic.ToString());
            AccessTools.Field(buildType,"Schematic").SetValue(build,schematic);
            ProjectionCell[] cells=[new(0,0,default,"game:stone"),new(1,1,new(1,0,0),"game:torch-lit-up")];
            AccessTools.Field(buildType,"Cells").SetValue(build,cells);
            var extract=AccessTools.Method(assembly.GetType("VSDirector.Blueprints.DirectorProjectionDocument",true)!,"Extract");
            var editing=new CreationProjectionSettings{Version=2,Size=new(2,1,1)};editing.Edit(cells[1]).Offset=new(4,0,0);
            var subset=(BlueprintDocument)extract.Invoke(null,[build,editing,new HashSet<int>{1},"Extracted"])!;
            Check(subset.Metadata.Dimensions.X==1&&subset.Schematic["Indices"]![0]!.Value<uint>()==0,"Extracted region was not normalized");
            Check(subset.BlockEntities.Single().Position.X==0&&subset.BlockEntities.Single().CompatData.Single().Data["value"]!.Value<int>()==7,"Extracted region lost relocated compatibility data");
            Check(document.BlockEntities.Single().Position.X==1&&document.Schematic["Indices"]!.Count()==2,"Extraction mutated source blueprint");
            editing.Edit(cells[1]).Scale=new(2,2,2);bool transformRejected=false;
            try{extract.Invoke(null,[build,editing,new HashSet<int>{1},"Scaled"]);}catch(TargetInvocationException e){transformRejected=e.InnerException is InvalidDataException;}
            Check(transformRejected,"Native export silently discarded cell display scale");
            ((IDisposable)build).Dispose();
            PresetRoundtrip(assembly,root,archive);

            string native=Path.Combine(root,"native.json");File.WriteAllText(native,"""
                {"anchorOffset":{"x":1,"y":0,"z":0},"schematic":{"SizeX":2,"SizeY":1,"SizeZ":1,"Indices":[0],"BlockIds":[1],"BlockCodes":{"1":"game:stone"}}}
                """);
            var imported=codec.Read(native);
            Check(imported.Metadata.Name=="native"&&imported.Metadata.AnchorOffset.X==1,"Native schematic wrapper metadata was not imported");
            var validate=AccessTools.Method(typeof(BlueprintCodec).Assembly.GetType("VSDirector.Blueprints.DirectorBlueprintService",true)!,"Validate")!;
            validate.Invoke(null,[imported]);
            bool rejected=false;
            try{validate.Invoke(null,[new BlueprintDocument{Schematic=JObject.Parse("""
                {"SizeX":1,"SizeY":1,"SizeZ":1,"Indices":[1024],"BlockIds":[1],"BlockCodes":{"1":"game:stone"}}
                """)}]);}
            catch(TargetInvocationException e){rejected=e.InnerException is InvalidDataException;}
            Check(rejected,"Out-of-bounds schematic coordinate was accepted");

            string shape=Path.Combine(root,"shape.json");File.WriteAllText(shape,"{\"elements\":[]}");
            rejected=false;
            try{validate.Invoke(null,[codec.Read(shape)]);}
            catch(TargetInvocationException e){rejected=e.InnerException is InvalidDataException;}
            Check(rejected,"Shape model was mistaken for a block schematic");
            var encoder=AccessTools.Method(typeof(BlueprintCodec).Assembly.GetType("VSDirector.Blueprints.DirectorProjectionExport",true)!,"EncodeAsync");
            var locator=typeof(BlueprintCodec).Assembly.GetType("VSDirector.Recording.DirectorFfmpegLocator",true)!;
            string ffmpeg=(string)AccessTools.Method(locator,"ResolveRequired").Invoke(null,[""])!;
            foreach(bool apng in new[]{true,false})
            {
                var settings=new VSDirector.Data.CreationProjectionView{Width=16,Height=16,Fps=2,Duration=1,Apng=apng};
                var pixels=new byte[16*16*4*2];
                for(int frame=0;frame<2;frame++)for(int y=4;y<12;y++)for(int x=4;x<12;x++)
                {int offset=(frame*256+y*16+x)*4;pixels[offset+(frame==0?0:1)]=255;pixels[offset+3]=255;}
                string raw=Path.Combine(root,"input.rgba"),output=Path.Combine(root,apng?"output.apng":"output.gif"),temporary=output+".partial";
                File.WriteAllBytes(raw,pixels);
                ((Task)encoder.Invoke(null,[ffmpeg,raw,temporary,output,settings,CancellationToken.None])!).GetAwaiter().GetResult();
                byte[] encoded=File.ReadAllBytes(output);
                Check(apng?encoded.AsSpan(0,8).SequenceEqual(new byte[]{137,80,78,71,13,10,26,10}):System.Text.Encoding.ASCII.GetString(encoded,0,6)=="GIF89a","Animated image has wrong container signature");
                Check(!File.Exists(raw)&&!File.Exists(temporary),"Encoder left temporary files after publication");
                if(apng)Check(System.Text.Encoding.Latin1.GetString(encoded).Contains("acTL"),"APNG output has no animation control chunk");
            }
        }
        finally{Directory.Delete(root,true);}
        Console.WriteLine("PASS blueprint VSBP/native JSON round-trip, chisel/compatibility payloads, metadata, bounds, Shape rejection and real GIF/APNG encoding/cleanup");
    }
    private static void PresetRoundtrip(Assembly assembly,string directory,string archive)
    {
        var type=assembly.GetType("VSDirector.Runtime.DirectorCreationRuntime",true)!;
        var runtime=System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(type);
        var session=new DirectorCreationSession(new DirectorCreationStore(Path.Combine(directory,"project-store")));
        AccessTools.Field(type,"<Session>k__BackingField").SetValue(runtime,session);
        var project=new DirectorCreationProject();session.Open(project);
        var asset=session.Store.Import(project,archive,CreationAssetKind.Structure);
        var root=new CreationSceneObject{Kind=CreationObjectKind.Structure,AssetId=asset.Id,Projection=new(){Version=2,Size=new(2,1,1)}};
        var child=new CreationSceneObject{Kind=CreationObjectKind.Particle,ParentId=root.Id,ParticleEffect=CreationParticleEffect.Create()};
        var effect=CreationParticleEffect.Create();effect.Emitters[0].Modules=new(){BindObject=root.Id};root.Projection.ParticleSchemes["scheme"]=effect;
        var texture=new CreationAsset{Kind=CreationAssetKind.Texture,Source="assets/test.png"};project.Assets.Add(texture);
        File.WriteAllBytes(session.Store.AssetPath(project.Id,texture.Source),[137,80,78,71]);effect.Emitters[0].Texture=texture.Source;
        project.Objects.AddRange([root,child]);project.Tracks.Add(new(){ObjectId=child.Id,Channel=CreationChannel.Position,Keys=[new(){Time=1,Value=CreationVector.One}]});project.Validate();
        string path=Path.Combine(directory,"structure.director-structure.json");AccessTools.Method(type,"ExportProjectionPreset").Invoke(runtime,[root,path]);
        var presetType=type.GetNestedType("ProjectionPreset",BindingFlags.NonPublic)!;
        var preset=System.Text.Json.JsonSerializer.Deserialize(File.ReadAllText(path),presetType,CreationJson.Options)!;
        var imported=(CreationSceneObject)AccessTools.Method(type,"ImportProjectionScene").Invoke(runtime,[preset,new CreationVector(10,0,0)])!;
        var newChild=project.Objects.Single(o=>o.ParentId==imported.Id);
        if(imported.Id==root.Id||newChild.Id==child.Id||!project.Tracks.Any(t=>t.ObjectId==newChild.Id))throw new Exception("Portable structure did not remap child animation");
        var node=imported.Projection!.ParticleSchemes["scheme"].Emitters[0];
        if(node.Modules!.BindObject!=imported.Id||node.Texture==texture.Source||!File.Exists(session.Store.AssetPath(project.Id,node.Texture)))throw new Exception("Portable structure lost texture or particle binding");
        if(Math.Abs(DirectorCreationEvaluator.Evaluate(project,0).Objects[imported.Id].World.Translation.X-10)>1e-5)throw new Exception("Portable structure placement was not applied");
        project.Validate();Console.WriteLine("PASS structure subset payload preservation, static export guards and portable scene assets/bindings/child tracks");
    }

}
