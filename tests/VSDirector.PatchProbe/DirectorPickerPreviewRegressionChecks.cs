using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using HarmonyLib;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using VSDirector.Data;
using Vintagestory.Client.NoObf;

internal static partial class DirectorImGuiProbe
{
    private sealed class PickerPlayer : EntityPlayer
    {
        internal ITexPositionSource Source = null!;
        public override ITexPositionSource GetTextureSource() => Source;
    }

    private static void CheckPickerPreviewRegressions(Type workspaceType)
    {
        var assembly = workspaceType.Assembly;
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        var runtimeType = assembly.GetType("VSDirector.Runtime.DirectorCreationRuntime", true)!;
        var runtime = RuntimeHelpers.GetUninitializedObject(runtimeType);
        string raw = """
            {"textureWidth":32,"textureHeight":32,"textures":{"skin":"entity/seraph"},"elements":[
              {"name":"body","from":[0,0,0],"to":[16,32,16],"faces":{"north":{"texture":"#skin","uv":[0,0,16,32]}}}]}
            """;
        var props = new EntityProperties { Code = new("game:player"), Client = new([],new()) { Shape = new() { Base = new("game:player-test") } } };
        var logger = ProbeDispatchProxy.Create<ILogger>((m,_) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var actor = new PickerPlayer { Code = props.Code, EntityId = 123 };
        AccessTools.Property(typeof(Entity),"Properties").SetValue(actor,props);
        actor.Source = ProbeDispatchProxy.Create<ITexPositionSource>((m,_) => m.Name == "get_AtlasSize" ? new Size2i(32,32) : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var player=(ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
        var playerData=(ClientWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldPlayerData));
        AccessTools.Field(typeof(ClientWorldPlayerData),"entityplayer").SetValue(playerData,actor);
        AccessTools.Field(typeof(ClientPlayer),"worlddata").SetValue(player,playerData);
        var world = ProbeDispatchProxy.Create<IClientWorldAccessor>((m,_) => m.Name switch { "get_Player"=>player, "GetEntityType"=>props, "GetEntityById"=>actor, _=>ProbeDispatchProxy.DefaultValue(m.ReturnType) });
        var asset = ProbeDispatchProxy.Create<IAsset>((m,_) => m.Name == "ToText" ? raw : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        int reads = 0;
        var assets = ProbeDispatchProxy.Create<IAssetManager>((m,_) => { if(m.Name=="Get"){reads++;return asset;}return ProbeDispatchProxy.DefaultValue(m.ReturnType); });
        bool resolved=false,playerTexture=false;
        ITexPositionSource? usedTextures = null;
        var tess = ProbeDispatchProxy.Create<ITesselatorAPI>((m,args) =>
        {
            if(m.Name=="TesselateShape" && args![0] is TesselationMetaData meta)
            {
                var shape=(Shape)args[1]!;
                resolved=shape.Elements[0].FacesResolved?.Any(f=>f is not null)==true;
                playerTexture=ReferenceEquals(meta.TexSource,actor.Source);
                usedTextures = meta.TexSource;
                var mesh=new MeshData(4,6);mesh.AddVertex(0,0,0,0,0);args[2]=mesh;
            }
            return ProbeDispatchProxy.DefaultValue(m.ReturnType);
        });
        var expectedPosition = new TextureAtlasPosition { atlasTextureId = 1234 };
        var atlasPositions = new[] { new TextureAtlasPosition(), expectedPosition };
        int inserts = 0;
        var entityAtlas = ProbeDispatchProxy.Create<ITextureAtlasAPI>((m,_) =>
        {
            if (m.Name == "GetOrInsertTexture") inserts++;
            return m.Name switch { "get_Positions" => atlasPositions, "get_Size" => new Size2i(1024, 1024), _ => ProbeDispatchProxy.DefaultValue(m.ReturnType) };
        });
        var api=ProbeDispatchProxy.Create<ICoreClientAPI>((m,_)=>m.Name switch {"get_World"=>world,"get_Assets"=>assets,"get_Tesselator"=>tess,"get_Logger"=>logger,"get_EntityTextureAtlas"=>entityAtlas,_=>ProbeDispatchProxy.DefaultValue(m.ReturnType)});
        AccessTools.Field(runtimeType,"api").SetValue(runtime,api);
        AccessTools.Method(runtimeType,"ActorPickerMesh").Invoke(runtime,["game:player",0L]);
        Check(resolved&&playerTexture&&reads==1,"Raw player preview did not resolve faces or use actual player skin source");
        props.Client.LoadedShape=JsonUtil.FromString<Shape>(raw)!;props.Client.LoadedShape.ResolveReferences(logger,"probe");
        AccessTools.Method(runtimeType,"ActorPickerMesh").Invoke(runtime,["game:player",0L]);
        Check(reads==1,"Preview ignored the already loaded native shape");

        var nativeTexture = new CompositeTexture { Base = new("game:entity/test"), Baked = new()
            { TextureSubId = 0, BakedVariants = [new() { TextureSubId = 1 }] } };
        props.Client.Textures["all"] = nativeTexture;
        AccessTools.Method(runtimeType,"ActorPickerMesh").Invoke(runtime,["game:catalogue-animal",0L]);
        Check(usedTextures is not null && ReferenceEquals(usedTextures["skin"], expectedPosition)
            && ReferenceEquals(usedTextures["all"], expectedPosition) && usedTextures.AtlasSize?.Width == 1024,
            "Catalogue preview did not use the entity atlas, native texture variant or all fallback");
        Check(inserts == 0 && nativeTexture.Baked.TextureSubId == 0 && nativeTexture.Baked.BakedVariants[0].TextureSubId == 1,
            "Catalogue preview rebaked or modified an already loaded entity texture");

        // Read the real game's fire configuration, whose emitters all start with Quantity=0.
        string game=Path.GetDirectoryName(typeof(Entity).Assembly.Location)!;
        string file=Path.Combine(game,"assets","survival","blocktypes","fire.json");
        if(!File.Exists(file))file=Path.Combine(Environment.GetEnvironmentVariable("VINTAGE_STORY")??@"E:\vintagestory\Vintagestory","assets","survival","blocktypes","fire.json");
        if(!File.Exists(file))throw new Exception("Fire fixture not found: "+file);
        var root=JsonNode.Parse(JsonUtil.ToString(JsonUtil.FromString<object>(File.ReadAllText(file))))!.AsObject();
        var definitions=CreationParticleFields.Find(root,"particleProperties")!.AsArray();
        var resourceType=assembly.GetType("VSDirector.Runtime.DirectorParticleResource",true)!;
        var resources=Array.CreateInstance(resourceType,definitions.Count);
        for(int i=0;i<definitions.Count;i++)resources.SetValue(Activator.CreateInstance(resourceType,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,
            ["fire/"+i,"game:fire","game:fire","game:fire","block",i,definitions[i]!.DeepClone(),null],null),i);
        var previewType=workspaceType.GetNestedType("ParticleCardPreview",BindingFlags.NonPublic)!;
        using var preview=(IDisposable)Activator.CreateInstance(previewType,BindingFlags.Public|BindingFlags.NonPublic|BindingFlags.Instance,null,[resources],null)!;
        AccessTools.Method(previewType,"Advance").Invoke(preview,null);
        var simulation=(DirectorCreationParticleSimulation)AccessTools.Property(previewType,"Simulation").GetValue(preview)!;
        // Native fractional quantities can legitimately emit nothing at t=0.
        // Drive the thumbnail clock through a bounded interval without wall-clock waits.
        for(int frame=0;frame<60&&simulation.Particles.Count==0;frame++)
        {
            AccessTools.Field(previewType,"clock").SetValue(preview,ImGuiNET.ImGui.GetTime()-.05);
            AccessTools.Method(previewType,"Advance").Invoke(preview,null);
        }
        Check((bool)AccessTools.Property(previewType,"SampleEmission").GetValue(preview)! && simulation.Particles.Count>0,"game:fire still has no demonstration preview");
        Check(definitions.All(d=>CreationParticleFields.Number(CreationParticleFields.Find(d!.AsObject(),"quantity")!.AsObject(),"avg")==0),"Preview changed fire source quantities");

        CheckPickerDepth(assembly);
        Console.WriteLine("PASS raw/player loaded shape resolution and skin source; catalogue entity atlas/variants/all fallback; real game:fire preview emits without mutating source; GPU depth/alpha/rotation and state restoration");
    }

    private static void CheckPickerDepth(Assembly assembly)
    {
        var type=assembly.GetType("VSDirector.Rendering.DirectorPickerMeshPreview",true)!;
        using var renderer=(IDisposable)Activator.CreateInstance(type,true)!;
        var stateType=assembly.GetType("VSDirector.Rendering.DirectorCreationRenderState",true)!;
        using var state=(IDisposable)Activator.CreateInstance(stateType,true)!;
        int texture=GL.GenTexture();
        try
        {
            GL.BindTexture(TextureTarget.Texture2D,texture);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba8,1,1,0,PixelFormat.Rgba,PixelType.UnsignedByte,new byte[]{255,255,255,255});
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Nearest);
            var mesh=new MeshData(12,18);
            void Quad(float z,byte[] color)
            {
                int start=mesh.VerticesCount;
                mesh.AddVertex(-1,-1,z,0,0,color);mesh.AddVertex(1,-1,z,1,0,color);mesh.AddVertex(1,1,z,1,1,color);mesh.AddVertex(-1,1,z,0,1,color);
                foreach(int index in new[]{0,1,2,0,2,3})mesh.AddIndex(start+index);
            }
            Quad(.5f,[255,0,0,255]); Quad(-.5f,[0,0,255,255]); Quad(.6f,[0,255,0,0]);
            mesh.TextureIds=[texture];mesh.TextureIndices=[0,0,0];mesh.TextureIndicesCount=3;
            AccessTools.Method(type,"SetMesh").Invoke(renderer,[mesh]);
            int fbo=GL.GetInteger(GetPName.DrawFramebufferBinding),program=GL.GetInteger(GetPName.CurrentProgram),vao=GL.GetInteger(GetPName.VertexArrayBinding);
            int[] viewport=new int[4];GL.GetInteger(GetPName.Viewport,viewport);
            AccessTools.Method(type,"Render").Invoke(renderer,[0d,1f]);
            int[] after=new int[4];GL.GetInteger(GetPName.Viewport,after);
            if(fbo!=GL.GetInteger(GetPName.DrawFramebufferBinding)||program!=GL.GetInteger(GetPName.CurrentProgram)||vao!=GL.GetInteger(GetPName.VertexArrayBinding)||!viewport.SequenceEqual(after))throw new Exception("Actor preview leaked GL state");
            byte[] Read()
            {
                GL.BindTexture(TextureTarget.Texture2D,(int)AccessTools.Property(type,"Texture").GetValue(renderer)!);
                var pixels=new byte[256*256*4];GL.GetTexImage(TextureTarget.Texture2D,0,PixelFormat.Rgba,PixelType.UnsignedByte,pixels);return pixels;
            }
            var first=Read();int center=(128*256+128)*4;
            if(first[center]<120||first[center+1]>5||first[center+2]>5||first[center+3]<250)throw new Exception("Actor preview depth or transparent cutout is wrong");
            if(first[3]!=0)throw new Exception("Actor preview background is not transparent");
            AccessTools.Method(type,"Render").Invoke(renderer,[3d,1f]);
            if(first.SequenceEqual(Read()))throw new Exception("Actor preview did not rotate");
            var partType=assembly.GetType("VSDirector.Blueprints.BlueprintMeshPart",true)!;
            using var part=(IDisposable)Activator.CreateInstance(partType,[mesh,false])!;
            var parts=Array.CreateInstance(partType,1);parts.SetValue(part,0);
            var projectionView=new VSDirector.Data.CreationProjectionView{Yaw=0,Pitch=0,Duration=2};
            AccessTools.Field(type,"ProjectionView").SetValue(renderer,projectionView);
            AccessTools.Method(type,"SetProjection").Invoke(renderer,[parts,System.Numerics.Matrix4x4.Identity]);
            AccessTools.Method(type,"SetSize").Invoke(renderer,[128,96]);
            byte[] ProjectionPixels(double time)
            {
                AccessTools.Method(type,"Render").Invoke(renderer,[time,128f/96]);
                return (byte[])AccessTools.Method(type,"ReadRgba").Invoke(renderer,null)!;
            }
            var projectionStart=ProjectionPixels(0);
            if(projectionStart.Length!=128*96*4||projectionStart[3]!=0)throw new Exception("Projection dimensions or transparent background are wrong");
            if(!projectionStart.SequenceEqual(ProjectionPixels(2)))throw new Exception("Projection full turn did not return to the same image");
            if(projectionStart.SequenceEqual(ProjectionPixels(.5)))throw new Exception("Projection automatic rotation did not change image");
            projectionView.Transparent=false;projectionView.Background=new(.2,.3,.4);
            var background=ProjectionPixels(0);
            if(background[3]!=255||Math.Abs(background[0]-51)>1||Math.Abs(background[1]-77)>1)throw new Exception("Projection background color/alpha are wrong");
            var oldCenter=AccessTools.Field(type,"projectionCenter").GetValue(renderer);var oldRadius=AccessTools.Field(type,"projectionRadius").GetValue(renderer);
            AccessTools.Field(type,"LockProjectionBounds").SetValue(renderer,true);
            AccessTools.Method(type,"SetProjection").Invoke(renderer,[Array.CreateInstance(partType,0),System.Numerics.Matrix4x4.Identity]);
            if(!Equals(oldCenter,AccessTools.Field(type,"projectionCenter").GetValue(renderer))||!Equals(oldRadius,AccessTools.Field(type,"projectionRadius").GetValue(renderer)))throw new Exception("Growth to zero cells changed preview camera framing");
            Console.WriteLine("PASS projection GPU mesh composition, fixed growth framing, non-square resize/readback, transparency, background and full-turn rotation");
        }
        finally{GL.DeleteTexture(texture);}
    }
}
