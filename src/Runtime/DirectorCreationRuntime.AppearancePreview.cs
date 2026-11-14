using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;
using VSDirector.Data;

namespace VSDirector.Runtime;

internal sealed partial class DirectorCreationRuntime
{
    private Guid appearanceActorId;
    private EntityAgent? appearanceEntity;
    private ActorGuiRenderer? appearanceRenderer;
    private byte[]? appearanceSnapshot;
    private string appearanceEntityCode="";

    // Native entity mesh/texture rendering in the GUI, with synchronous mesh upload.
    // No world render pass, camera or asynchronous upload outliving the dialog.
    private sealed class ActorGuiRenderer(EntityAgent actor,ICoreClientAPI api) : EntityShapeRenderer(actor,api)
    {
        internal float Height { get; private set; }=2;
        internal float Width { get; private set; }=1;
        public override void TesselateShape()
        {
            var composite=actor.Properties.Client.Shape;
            var shape=actor.Properties.Client.LoadedShapeForEntity?.Clone()
                ??throw new InvalidOperationException("Actor shape is unavailable.");
            actor.OnTesselation(ref shape,composite.Base.ToString());
            defaultTexSource=actor.GetTextureSource();
            capi.Tesselator.TesselateShape(new TesselationMetaData
            {
                TexSource=this,WithJointIds=true,WithDamageEffect=true,TypeForLogging="director-character-gui",
                QuantityElements=composite.QuantityElements,SelectiveElements=composite.SelectiveElements,
                IgnoreElements=composite.IgnoreElements
            },shape,out var mesh);
            // EntityShapeRenderer's GUI matrix revolves about (.5, 0, .5).
            // Recenter the private mesh so clothing/offset shapes do not drift as
            // the player turns it; never use the scene object's world transform.
            if(mesh.VerticesCount>0)
            {
                float minX=float.MaxValue,minY=float.MaxValue,minZ=float.MaxValue;
                float maxX=float.MinValue,maxY=float.MinValue,maxZ=float.MinValue;
                for(int i=0;i<mesh.VerticesCount;i++)
                {
                    minX=Math.Min(minX,mesh.xyz[3*i]);maxX=Math.Max(maxX,mesh.xyz[3*i]);
                    minY=Math.Min(minY,mesh.xyz[3*i+1]);maxY=Math.Max(maxY,mesh.xyz[3*i+1]);
                    minZ=Math.Min(minZ,mesh.xyz[3*i+2]);maxZ=Math.Max(maxZ,mesh.xyz[3*i+2]);
                }
                mesh.Translate(.5f-(minX+maxX)/2,-minY,.5f-(minZ+maxZ)/2);
                Height=Math.Max(.1f,maxY-minY);
                Width=Math.Max(.1f,MathF.Sqrt((maxX-minX)*(maxX-minX)+(maxZ-minZ)*(maxZ-minZ)));
            }
            onMeshReady(mesh);
            actor.OnTesselated();
        }
    }
    private EntityAgent? AppearanceEntity()
    {
        var actor=Session.Project?.Objects.Find(o=>o.Id==appearanceActorId);
        if(actor?.ActorSource is not {} source)return null;
        byte[] snapshot=ActorAppearance(actor).ToBytes();
        if(appearanceEntity is not null&&appearanceEntityCode==source.EntityCode
            &&appearanceSnapshot is not null&&snapshot.AsSpan().SequenceEqual(appearanceSnapshot))return appearanceEntity;
        DisposeAppearanceEntity();
        var properties=api.World.GetEntityType(new AssetLocation(source.EntityCode))?.Clone()
            ??throw new InvalidOperationException("Actor entity type is unavailable.");
        foreach(string key in properties.Client.Textures.Keys.ToArray())
            properties.Client.Textures[key]=CloneAppearanceTexture(properties.Client.Textures[key]);
        var configs=properties.Client.BehaviorsAsJsonObj.Where(b=>b["code"].AsString()=="extraskinnable").ToList();
        configs.Insert(0,JsonObject.FromJson("{\"code\":\"seraphinventory\"}"));
        properties.Client.BehaviorsAsJsonObj=configs.ToArray();
        var entity=new EntityAgent{EntityId=-Math.Abs(DateTime.UtcNow.Ticks),Code=properties.Code};
        bool textureAllocated=false;
        try
        {
            entity.WatchedAttributes.FromBytes(snapshot);
            if(entity.WatchedAttributes.GetTreeAttribute("wearablesInv") is {} clothes)
                entity.WatchedAttributes["seraphinventory"]=clothes.Clone();
            entity.Pos.SetPos(api.World.Player.Entity.Pos);
            entity.Initialize(properties,api,0);
            appearanceRenderer=new ActorGuiRenderer(entity,api);
            properties.Client.Renderer=appearanceRenderer;
            foreach(var behavior in properties.Client.Behaviors)behavior.OnEntityLoaded();
            entity.GetTextureSource();textureAllocated=true;
            appearanceRenderer.OnEntityLoaded();
            appearanceRenderer.TesselateShape();
            appearanceEntity=entity;appearanceSnapshot=snapshot;appearanceEntityCode=source.EntityCode;
            return entity;
        }
        catch
        {
            appearanceRenderer?.Dispose();appearanceRenderer=null;
            foreach(var behavior in properties.Client.Behaviors)
                if(behavior is not EntityBehaviorTexturedClothing||textureAllocated)
                    behavior.OnEntityDespawn(new EntityDespawnData{Reason=EnumDespawnReason.Removed});
            throw;
        }
    }
    private void DisposeAppearanceEntity()
    {
        appearanceRenderer?.Dispose();appearanceRenderer=null;
        if(appearanceEntity is {} entity)
            foreach(var behavior in entity.Properties.Client.Behaviors)
                behavior.OnEntityDespawn(new EntityDespawnData{Reason=EnumDespawnReason.Removed});
        appearanceEntity=null;appearanceSnapshot=null;appearanceEntityCode="";
    }
    private void DrawAppearanceEntity(float dt,ElementBounds bounds,float yaw,float pitch,float zoom)
    {
        if(AppearanceEntity() is not {} entity||appearanceRenderer is not {} renderer)return;
        if(entity.GetBehavior<EntityBehaviorTexturedClothing>() is {} clothing)
        {
            bool naked=(nativeAppearanceDialog as VSDirector.Gui.DirectorActorItemDialog)?.PreviewNaked==true;
            if(clothing.hideClothing!=naked){clothing.hideClothing=naked;renderer.TesselateShape();}
        }
        RenderAppearanceEntity(api,entity,dt,bounds,yaw,pitch,zoom,renderer.Width,renderer.Height);
    }
    private static void RenderAppearanceEntity(ICoreClientAPI api,Entity entity,float dt,ElementBounds bounds,float yaw,float pitch,float zoom,float width,float height)
    {
        float nativeSize=Math.Max(.01f,entity.Properties.Client.Size);
        float size=(float)Math.Min(bounds.InnerHeight*.9/(height*nativeSize),bounds.InnerWidth*.9/(width*nativeSize))*zoom;
        // The native GUI renderer adds (size, 2*size) before drawing. Compensate
        // explicitly to place the feet and vertical centre inside the inset.
        double x=bounds.renderX+bounds.InnerWidth/2-size;
        double feet=bounds.renderY+(bounds.InnerHeight+height*nativeSize*size)/2;
        double centerX=bounds.renderX+bounds.InnerWidth/2,centerY=bounds.renderY+bounds.InnerHeight/2;
        double depth=GuiElement.scaled(250);
        api.Render.PushScissor(bounds);
        api.Render.GlPushMatrix();
        api.Render.GlTranslate(centerX,centerY,depth);
        api.Render.GlRotate(pitch*180/MathF.PI,1,0,0);
        api.Render.GlTranslate(-centerX,-centerY,-depth);
        var shader=api.Render.CurrentActiveShader;
        shader?.Uniform("lightPosition",-.7071068f,-.7071068f,0f);
        try {api.Render.RenderEntityToGui(dt,entity,x,feet-2*size,GuiElement.scaled(250),yaw,size,-1);}
        finally
        {
            api.Render.GlPopMatrix();
            api.Render.PopScissor();
            shader?.Uniform("normalShaded",0);
            shader?.Uniform("lightPosition",.7071068f,-.7071068f,0f);
        }
    }
}
