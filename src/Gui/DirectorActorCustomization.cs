using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using Vintagestory.Client.NoObf;

namespace VSDirector.Gui;

// Forward native UI reads to an unregistered actor facade. Never replace the
// world's player, run player initialization, or open a live player inventory.
public class DirectorActorApiProxy : DispatchProxy
{
    private object source=null!;
    private System.Func<MethodInfo,object?[]?,(bool Handled,object? Value)> route=null!;
    internal static T Wrap<T>(T source,System.Func<MethodInfo,object?[]?,(bool,object?)> route) where T:class
    {
        var proxy=Create<T,DirectorActorApiProxy>();
        var state=(DirectorActorApiProxy)(object)proxy;state.source=source;state.route=route;return proxy;
    }
    protected override object? Invoke(MethodInfo? method,object?[]? args)
    {
        var result=route(method!,args);
        if(result.Handled)return result.Value;
        try{return method!.Invoke(source,args);}
        catch(TargetInvocationException e) when(e.InnerException is not null)
        {System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e.InnerException).Throw();throw;}
    }
}

internal sealed class DirectorActorCustomization : GuiDialogCreateCharacter
{
    internal sealed class ActorPlayer : EntityPlayer
    {
        internal Action<string>? SelectClass;
        internal void SetProperties(EntityProperties properties)=>Properties=properties;
        public override string GetName()=>"Director actor";
    }
    private sealed class InventoryFacade(Entity entity,InventoryBase inventory):EntityBehaviorPlayerInventory(entity)
    {public override InventoryBase Inventory=>inventory;}
    private sealed class PreviewFacade(Entity entity,ICoreClientAPI api):EntityShapeRenderer(entity,api)
    {public override void TesselateShape() { }}
    private readonly Action<string,string> setSkin;
    private readonly Action<int> navigate;
    private readonly CharacterSystem classes;
    private readonly Func<EntityAgent> actor;
    private bool composing;
    internal bool Naked=>charNaked;
    internal DirectorActorCustomization(ICoreClientAPI api,InventoryBase inventory,Func<EntityAgent> actor,
        Action<string,string> setSkin,Action<string> setClass,Action<int> navigate)
        :base(CreateApi(api,inventory,actor),null!)
    {
        this.actor=actor;this.setSkin=setSkin;this.navigate=navigate;
        classes=api.ModLoader.GetModSystem<CharacterSystem>();
        ((ActorPlayer)capi.World.Player.Entity).SelectClass=code=>{if(!composing)setClass(code);};
        charNaked=false;
    }
    private static ICoreClientAPI CreateApi(ICoreClientAPI api,InventoryBase inventory,Func<EntityAgent> getActor)
    {
        var facade=new ActorPlayer{World=api.World,Api=api};
        EntityAgent? previous=null;
        ActorPlayer Get()
        {
            var actor=getActor();
            if(!ReferenceEquals(previous,actor))
            {
                previous=actor;facade.SetProperties(new EntityProperties{Client=new EntityClientProperties([],null!)});
                facade.WatchedAttributes.FromBytes(actor.WatchedAttributes.ToBytes());
                facade.Properties.Client.Behaviors=[new InventoryFacade(facade,inventory),actor.GetBehavior<EntityBehaviorExtraSkinnable>()];
                // Composer-only renderer: no native listeners or GPU resources.
                facade.Properties.Client.Renderer=(PreviewFacade)RuntimeHelpers.GetUninitializedObject(typeof(PreviewFacade));
            }
            return facade;
        }
        // IPlayerInventoryManager contains internal interface methods and cannot
        // be proxied with DispatchProxy. Use a private native inventory owner.
        var player=(ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
        var data=(ClientWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldPlayerData));
        data.PlayerUID="vsdirector-actor-customization";data.PlayerName="Director actor";
        AccessTools.Field(typeof(ClientWorldPlayerData),"entityplayer").SetValue(data,facade);
        AccessTools.Field(typeof(ClientPlayer),"worlddata").SetValue(player,data);
        var manager=new ClientPlayerInventoryManager(new Vintagestory.API.Datastructures.OrderedDictionary<string,InventoryBase>
            {{"character-"+data.PlayerUID,inventory}},player,null!);
        AccessTools.Field(typeof(ClientPlayer),"inventoryMgr").SetValue(player,manager);
        var world=DirectorActorApiProxy.Wrap(api.World,(m,_)=>
        {if(m.Name!="get_Player")return(false,null);Get();return(true,(object?)player);});
        return DirectorActorApiProxy.Wrap(api,(m,_)=>m.Name=="get_World"?(true,(object?)world):(false,null));
    }
    internal (GuiComposer Composer,ElementBounds Preview) Build(int tab)
    {
        AccessTools.Field(typeof(GuiDialogCreateCharacter),"curTab").SetValue(this,tab);
        // Native randomize/last-selection mutate player data directly. Skin
        // controls use our virtual callbacks; class descriptions remain native.
        AccessTools.Field(typeof(GuiDialogCreateCharacter),"modSys").SetValue(this,tab==1?classes:null);
        string code=actor().WatchedAttributes.GetString("characterClass","commoner");
        AccessTools.Field(typeof(GuiDialogCreateCharacter),"currentClassIndex").SetValue(this,Math.Max(0,classes.characterClasses.FindIndex(c=>c.Code==code)));
        composing=true;
        try {ComposeGuis();}
        finally {composing=false;}
        var composer=Composers["createcharacter"];
        var tabs=composer.GetHorizontalTabs("tabs");
        AccessTools.Field(typeof(GuiElementHorizontalTabs),"handler").SetValue(tabs,(Action<int>)(i=>navigate(i+1)));
        return (composer,insetSlotBounds);
    }
    protected override void onToggleSkinPart(string code,string variant)=>setSkin(code,variant);
    protected override void onToggleSkinPart(string code,int index)
    {
        var variants=actor().GetBehavior<EntityBehaviorExtraSkinnable>()!.AvailableSkinPartsByCode[code].Variants
            .Where(v=>variantCategories.Contains(v.Category)).ToArray();
        if(index>=0&&index<variants.Length)setSkin(code,variants[index].Code);
    }
    protected override bool OnNext(){navigate(0);return true;}
    protected override void OnTitleBarClose()=>navigate(0);
    public override bool TryClose(){navigate(0);return true;}
    public override void OnGuiOpened() { }
    public override void OnGuiClosed() { }
}

[HarmonyPatch(typeof(CharacterSystem),nameof(CharacterSystem.setCharacterClass))]
internal static class DirectorActorClassIsolationPatch
{
    private static bool Prefix(EntityPlayer eplayer,string classCode)
    {
        if(eplayer is not DirectorActorCustomization.ActorPlayer actor)return true;
        actor.SelectClass?.Invoke(classCode);return false;
    }
}

internal sealed partial class DirectorActorItemDialog
{
    private DirectorActorCustomization? customization;
    private int customizationTab;
    private ElementBounds? equipmentPreview;
    internal bool PreviewNaked=>customizationTab==1 && customization?.Naked==true;
    private bool CustomizationDropdownOpen()=>Composers["customization"] is {} panel &&
        Elements(panel,"interactiveElements").Values.OfType<GuiElementDropDown>().Any(d=>d.listMenu.IsOpened);
    internal void ConfigureCustomization(Func<EntityAgent> actor,Action<string,string> skin,Action<string> characterClass)
    {
        using var scale=UseScale();
        customization=new DirectorActorCustomization(capi,equipment,actor,skin,characterClass,QueueCustomizationTab);
        equipmentPreview=previewBounds;
        ReplaceTabs(Composers["character"],0);
    }
    private void ReplaceTabs(GuiComposer composer,int selected)
    {
        var old=composer.GetHorizontalTabs("tabs");
        var tabs=new GuiElementHorizontalTabs(capi,[
            new(){Name=Lang.Get("charactertab-character"),DataInt=0},
            new(){Name=Lang.Get("tab-skinandvoice"),DataInt=1},
            new(){Name=Lang.Get("tab-charclass"),DataInt=2}],
            CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold),CairoFont.WhiteSmallText().WithWeight(Cairo.FontWeight.Bold),old.Bounds,QueueCustomizationTab)
            {activeElement=selected};
        Elements(composer,"interactiveElements")["tabs"]=tabs;
        Elements(composer,"staticElements")["tabs"]=tabs;old.Dispose();composer.ReCompose();
    }
    private void QueueCustomizationTab(int tab)
        =>capi.Event.EnqueueMainThreadTask(()=>
        {
            if(!IsOpened())return;
            try{ShowCustomizationTab(tab);}catch(Exception e){failed(e);}
        },"vsdirector-actor-customization");
    private void ShowCustomizationTab(int tab)
    {
        if(customization is null)return;
        using var scale=UseScale();
        held=null;rotating=false;orbitingWorld=false;renderFailed=false;
        if(tab==0)
        {
            if(Composers["customization"] is {} old)old.Enabled=false;
            foreach(string key in new[]{"character","creative","hotbar"})Composers[key].Enabled=true;
            previewBounds=equipmentPreview!;customizationTab=0;RefreshEquipment();
            Composers["character"].GetHorizontalTabs("tabs").SetValue(0,false);return;
        }
        ComposingNative=this;
        try
        {
            var native=customization.Build(tab-1);
            Composers["customization"]=native.Composer;previewBounds=native.Preview;
            ReplaceTabs(native.Composer,tab);
            // The native confirm/title controls return to the character tab.
            foreach(var bar in Elements(native.Composer,"interactiveElements").Values.OfType<GuiElementDialogTitleBar>())
                Field(bar,"OnClose",(Action)(()=>QueueCustomizationTab(0)));
            foreach(string key in new[]{"character","creative","hotbar"})Composers[key].Enabled=false;
            customizationTab=tab;
        }
        finally{ComposingNative=null;}
    }
}
