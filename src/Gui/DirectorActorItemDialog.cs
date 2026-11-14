using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.Client.NoObf;
using Vintagestory.Common;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed record ActorEquipmentTarget(string Name, Func<ItemStack?> Read, System.Func<ItemStack,bool> Accepts, Action<ItemStack?> Write, string Icon="shirt");

// Calls the game's window composers. Only inventory routing belongs to Director:
// the native windows must never open, send packets or acquire the player's cursor.
internal sealed partial class DirectorActorItemDialog : GuiDialog
{
    private sealed class NativeCharacter(ICoreClientAPI api, IInventory source) : GuiDialogCharacter(api)
    {
        protected override void ComposeCharacterTab(GuiComposer composer)
        {
            characterInv=source;
            base.ComposeCharacterTab(composer);
        }
        // The sole native character tab is already composed; do not let its
        // callback replace our isolated controls with live inventory handlers.
        protected override void ComposeGuis() { }
        internal (GuiComposer Composer,ElementBounds Preview) Build()
        {
            base.ComposeGuis();
            var result=Composers["playercharacter"];
            Composers.Remove("playercharacter");
            return (result,insetSlotBounds);
        }
    }
    // Native rendering, hit bounds, tooltips and textures are retained; only item
    // manipulation is intercepted so no live inventory/cursor can be changed.
    private sealed class ActorGrid : GuiElementItemSlotGrid
    {
        internal Action<int,EnumMouseButton>? Clicked;
        internal ActorGrid(ICoreClientAPI api,IInventory inventory,int columns,int[]? slots,ElementBounds bounds)
            :base(api,inventory,_=>{},columns,slots,bounds){KeyboardControlEnabled=false;}
        internal int At(int x,int y)
        {
            if(InsideClipBounds is {} clip&&!clip.PointInside(x,y))return -1;
            if(!Bounds.ParentBounds.PointInside(x,y))return -1;
            for(int i=0;i<renderedSlots.Count&&i<SlotBounds.Length;i++)
                if(SlotBounds[i].PointInside(x,y))return renderedSlots.GetKeyAtIndex(i);
            return -1;
        }
        public override void OnMouseDownOnElement(ICoreClientAPI api,MouseEvent args)
        {
            int slot=At(args.X,args.Y);
            if(slot>=0)
            {
                Clicked?.Invoke(slot,args.Button);
                args.Handled=true;
            }
        }
        public override void SlotClick(ICoreClientAPI api,int slotId,EnumMouseButton button,bool shift,bool ctrl,bool alt)
        {
            Clicked?.Invoke(slotId,button);
        }
        public override void OnMouseUp(ICoreClientAPI api,MouseEvent args) { }
        public override void OnMouseWheel(ICoreClientAPI api,MouseWheelEventArgs args) { }
    }
    [ThreadStatic] internal static DirectorActorItemDialog? ComposingNative;
    private readonly float windowScale;
    private readonly struct ScaleScope : IDisposable
    {
        private readonly float previous;
        internal ScaleScope(float value){previous=RuntimeEnv.GUIScale;RuntimeEnv.GUIScale=value;}
        public void Dispose()=>RuntimeEnv.GUIScale=previous;
    }
    private ScaleScope UseScale()=>new(windowScale);
    private readonly IReadOnlyList<ActorEquipmentTarget> targets;
    private readonly InventoryGeneric equipment,hotbar;
    private readonly InventoryPlayerCreative creative;
    private readonly List<(ActorGrid Grid,int Offset)> equipmentGrids=[];
    private ActorGrid catalogGrid=null!,hotbarGrid=null!;
    private ElementBounds previewBounds=null!;
    private readonly Action<float,ElementBounds,float,float,float> renderActor;
    private float actorYaw=-MathF.PI/2,actorPitch,actorZoom=1;
    private int orbitMouseX;
    internal Action<float>? ZoomWorld { get; set; }
    internal System.Func<int,int,bool>? CanOrbitWorld { get; set; }
    internal Action<float,float>? OrbitWorld { get; set; }
    private bool orbitingWorld;
    private int worldMouseX, worldMouseY;
    private readonly Dictionary<int,ItemStack?> pendingEquipment = new();
    private bool equipmentTaskQueued;
    private bool renderFailed;
    private readonly Action<Exception> failed;
    private int target,pressX,pressY,selectedHotbar;
    private ItemStack? held;
    private bool rotating;
    private float orbitIdleSeconds;
    private readonly GuiDialogInventory creativeBuilder;
    private readonly NativeCharacter characterBuilder;
    public override string ToggleKeyCombinationCode=>null!;
    public override bool UnregisterOnClose=>true;
    public override bool PrefersUngrabbedMouse=>true;
    public override bool CaptureAllInputs()=>true;
    public override double DrawOrder=>.86;
    private static bool ContainsNativeWindow(GuiComposer composer,int x,int y)
        => composer.Enabled && (composer.Bounds.PointInside(x,y) || Elements(composer,"interactiveElements").Values
            .Any(e=>e is GuiElementVerticalTabs or GuiElementHorizontalTabs && e.Bounds.PointInside(x,y)));
    private bool ContainsWindow(int x,int y)=>Composers.Values.Any(c=>ContainsNativeWindow(c,x,y));
    private static Dictionary<string,GuiElement> Elements(GuiComposer composer,string field)
        =>(Dictionary<string,GuiElement>)AccessTools.Field(typeof(GuiComposer),field).GetValue(composer)!;
    private static void Field(object instance,string field,object? value)
        =>AccessTools.Field(instance.GetType(),field).SetValue(instance,value);
    private static object? Call(object instance,string method,params object[] args)
        =>AccessTools.Method(instance.GetType(),method).Invoke(instance,args);

    internal DirectorActorItemDialog(ICoreClientAPI api,string title,IReadOnlyList<ActorEquipmentTarget> targets,int selected,
        Action<float,ElementBounds,float,float,float> renderActor,Action<Exception> failed):base(api)
    {
        // Fit the pair of original windows on small/high-DPI viewports without
        // changing the game's saved GUI scale or resizing individual controls.
        windowScale=Math.Min(RuntimeEnv.GUIScale,Math.Min(api.Render.FrameWidth/1200f,api.Render.FrameHeight/800f));
        using var scale=UseScale();
        this.targets=targets;this.renderActor=renderActor;this.failed=failed;
        target=Math.Clamp(selected,0,targets.Count-1);
        equipment=new InventoryGeneric(15,"vsdirector-actor-character",api);
        hotbar=new InventoryGeneric(12,"vsdirector-actor-hotbar",api);
        selectedHotbar=Math.Clamp(api.World.Player.InventoryManager.ActiveHotbarSlotNumber,0,9);
        var liveBar=api.World.Player.InventoryManager.GetOwnInventory("hotbar");
        for(int i=0;i<10;i++)hotbar[i].Itemstack=liveBar is not null&&i<liveBar.Count?liveBar[i]?.Itemstack?.Clone():null;
        RefreshEquipment();
        // Build a private copy of the real creative tabs (including modded items).
        creative=new InventoryPlayerCreative("vsdirector-actor-creative",api);
        if(api.World.Player.InventoryManager.GetOwnInventory("creative") is InventoryPlayerCreative liveCreative&&liveCreative.CreativeTabs.Tabs.Any())
        {
            foreach(var source in liveCreative.CreativeTabs.Tabs)
            {
                var tab=(CreativeTab)Call(creative,"CreateTab",source.Code,source.Inventory.Select(slot=>slot.Itemstack?.Clone()).Where(stack=>stack is not null).Cast<ItemStack>().ToList())!;
                if(source.SearchCache is not null&&source.SearchCacheNames is not null&&tab.Inventory.Count==source.Inventory.Count)
                {tab.SearchCache=new(source.SearchCache);tab.SearchCacheNames=new(source.SearchCacheNames);}
                creative.CreativeTabs.Add(tab);
            }
            creative.SetTab(0);
        }
        else Call(creative,"UpdateFromWorld",api.World);
        foreach(var tab in creative.CreativeTabs.Tabs)
            if(tab.SearchCache is null||tab.SearchCacheNames is null)tab.CreateSearchCache(api.World);
        characterBuilder=new NativeCharacter(api,equipment);
        // GuiDialogInventory's constructor registers global hotkeys and game-mode
        // listeners. This instance is a composer host only, never a live dialog.
        // GuiDialog's two constructor fields and the builder's inputs are initialized
        // explicitly; no constructor hooks, world changes or inventory packets run.
        creativeBuilder=(GuiDialogInventory)RuntimeHelpers.GetUninitializedObject(typeof(GuiDialogInventory));
        Field(creativeBuilder,"capi",api);
        creativeBuilder.Composers=new DlgComposers(creativeBuilder);
        Field(creativeBuilder,"creativeInv",creative);
        double width=api.Render.FrameWidth/Math.Max(.5,RuntimeEnv.GUIScale);
        Field(creativeBuilder,"cols",Math.Clamp((int)((width-800)/51),4,15));
        try {Compose(title);}
        catch {Dispose();throw;}
    }
    private ActorGrid AdaptGrid(GuiComposer composer,string key)
    {
        var original=composer.GetSlotGrid(key);
        var inventory=(IInventory)AccessTools.Field(typeof(GuiElementItemSlotGridBase),"inventory").GetValue(original)!;
        int columns=(int)AccessTools.Field(typeof(GuiElementItemSlotGridBase),"cols").GetValue(original)!;
        var grid=new ActorGrid(capi,inventory,columns,original.availableSlots.Keys.ToArray(),original.Bounds)
        {TabIndex=original.TabIndex,InsideClipBounds=original.InsideClipBounds,RenderAsPremultipliedAlpha=original.RenderAsPremultipliedAlpha};
        Elements(composer,"interactiveElements")[key]=grid;
        Elements(composer,"staticElements")[key]=grid;
        original.Dispose();
        return grid;
    }
    private void BindWindow(GuiComposer composer,string? title=null)
    {
        foreach(var bar in Elements(composer,"interactiveElements").Values.OfType<GuiElementDialogTitleBar>())
        {
            Field(bar,"OnClose",(Action)(()=>TryClose()));
            if(title is not null)bar.Text=title;
        }
    }
    private void Compose(string title)
    {
        (GuiComposer Composer,ElementBounds Preview) native;
        ComposingNative=this;
        try {native=characterBuilder.Build();}
        finally {ComposingNative=null;}
        previewBounds=native.Preview;
        var character=Composers["character"]=native.Composer;
        BindWindow(character,title);
        foreach(string key in new[]{"leftSlots","rightSlots","armorSlotsHead","armorSlotsBody","armorSlotsLegs"})
        {
            var grid=AdaptGrid(character,key);
            grid.Clicked=(slot,button)=>EquipmentClick(slot+2,button);
            equipmentGrids.Add((grid,2));
        }
        character.ReCompose();
        ComposingNative=this;
        try {Call(creativeBuilder,"ComposeCreativeInvDialog");}
        finally {ComposingNative=null;}
        var catalog=(GuiComposer)AccessTools.Field(typeof(GuiDialogInventory),"creativeInvDialog").GetValue(creativeBuilder)!;
        Composers["creative"]=catalog;
        BindWindow(catalog);
        catalogGrid=AdaptGrid(catalog,"slotgrid");
        catalogGrid.Clicked=(slot,button)=>
        {
            if(button==EnumMouseButton.Right)held=null;
            else if(button==EnumMouseButton.Left)Grab(creative[slot].Itemstack);
        };
        catalog.GetTextInput("searchbox").OnTextChanged=Search;
        Field(catalog.GetScrollbar("scrollbar"),"onNewScrollbarValue",(Action<float>)(value=>
        {catalogGrid.Bounds.fixedY=10-GuiElementItemSlotGridBase.unscaledSlotPadding-value;catalogGrid.Bounds.CalcWorldBounds();}));
        foreach(string key in new[]{"verticalTabs","verticalTabsR"})
            if(catalog.GetElement(key) is GuiElementVerticalTabs tabs)
                Field(tabs,"handler",(Action<int,GuiTab>)((_,tab)=>
                {
                    creative.SetTab(tab.DataInt);catalogGrid.DetermineAvailableSlots();
                    Search(catalog.GetTextInput("searchbox").GetText());
                }));
        catalog.Bounds.WithAlignment(EnumDialogArea.RightMiddle).WithFixedAlignmentOffset(-GuiStyle.DialogToScreenPadding,-35);
        catalog.ReCompose();
        // Native slot widgets and offhand placement, without a second catalog,
        // page controls, skin toggle or custom action buttons.
        double pitch=GuiElementPassiveItemSlot.unscaledSlotSize+GuiElementItemSlotGridBase.unscaledSlotPadding;
        var barBounds=ElementBounds.Fixed(EnumDialogArea.CenterBottom,0,0,12*pitch+20,pitch+20).WithFixedAlignmentOffset(0,-8);
        var bar=capi.Gui.CreateCompo("vsdirector-actor-hotbar",barBounds)
            .AddShadedDialogBG(ElementBounds.Fill,withTitleBar:false)
            .AddItemSlotGrid(hotbar,_=>{},1,[11],ElementStdBounds.SlotGrid(EnumDialogArea.None,10,10,1,1),"offhandgrid")
            .AddItemSlotGrid(hotbar,_=>{},10,Enumerable.Range(0,10).ToArray(),ElementStdBounds.SlotGrid(EnumDialogArea.None,10+2*pitch,10,10,1),"hotbargrid");
        var offhand=AdaptGrid(bar,"offhandgrid");offhand.Clicked=(_,button)=>EquipmentClick(1,button);equipmentGrids.Add((offhand,-10));
        hotbarGrid=AdaptGrid(bar,"hotbargrid");
        hotbarGrid.Clicked=(slot,button)=>
        {
            target=0;selectedHotbar=slot;hotbarGrid.HighlightSlot(slot);
            if(held is not null){Apply(0,held);hotbar[slot].Itemstack=held.Clone();hotbar.MarkSlotDirty(slot);held=null;}
            else if(button==EnumMouseButton.Right){hotbar[slot].Itemstack=null;hotbar.MarkSlotDirty(slot);Apply(0,null);}
            else {Grab(hotbar[slot].Itemstack);Apply(0,hotbar[slot].Itemstack);}
        };
        Composers["hotbar"]=bar.Compose(false);
        hotbarGrid.HighlightSlot(selectedHotbar);
        Search("");
    }
    internal void PrepareNativeCreative(GuiComposer composer)
    {
        if(!composer.DialogName.EndsWith("inventory-creative",StringComparison.Ordinal)||composer.Composed)return;
        // The game initializes this scrollbar only after Compose. Its first
        // handle can therefore have zero height on some Cairo backends.
        composer.Bounds.CalcWorldBounds();
        var scroll=composer.GetScrollbar("scrollbar");
        scroll.Bounds.CalcWorldBounds();
        float visible=Math.Max(1,(float)(scroll.Bounds.InnerHeight/RuntimeEnv.GUIScale));
        scroll.SetHeights(visible,Math.Max(visible,(float)composer.GetSlotGrid("slotgrid").Bounds.fixedHeight+3));
    }
    private void Search(string text)
    {
        using var scale=UseScale();
        var composer=Composers["creative"];
        catalogGrid.FilterItemsBySearchText(text,creative.CurrentTab.SearchCache,creative.CurrentTab.SearchCacheNames);
        int cols=(int)AccessTools.Field(typeof(GuiElementItemSlotGridBase),"cols").GetValue(catalogGrid)!;
        double height=Math.Max(1,Math.Ceiling(catalogGrid.renderedSlots.Count/(double)cols))*(GuiElementPassiveItemSlot.unscaledSlotSize+GuiElementItemSlotGridBase.unscaledSlotPadding);
        catalogGrid.Bounds.fixedHeight=height;
        composer.GetScrollbar("scrollbar").SetNewTotalHeight((float)(height+3));
        composer.GetScrollbar("scrollbar").SetScrollbarPosition(0);
        composer.GetDynamicText("searchResults").SetNewText(Lang.Get("creative-searchresults",catalogGrid.renderedSlots.Count));
    }
    private void Grab(ItemStack? stack)
    {held=stack?.Clone();pressX=capi.Input.MouseX;pressY=capi.Input.MouseY;}
    private void QuickEquip(ItemStack? stack)
    {
        if(stack is null)return;
        int slot=Enumerable.Range(2,Math.Max(0,targets.Count-2)).FirstOrDefault(i=>targets[i].Accepts(stack),target<2?target:0);
        Apply(slot,stack);held=null;
    }
    private void UpdateCompatibleSlots()
    {
        if(customizationTab!=0)return;
        ItemStack? item=held;
        if(item is null)
        {
            int x=capi.Input.MouseX,y=capi.Input.MouseY;
            int slot=catalogGrid.At(x,y);
            if(slot>=0)item=creative[slot].Itemstack;
            else if((slot=hotbarGrid.At(x,y))>=0)item=hotbar[slot].Itemstack;
            else if((slot=EquipmentAt(x,y))>=0&&slot<targets.Count)item=targets[slot].Read();
        }
        bool clothing=item is not null&&targets.Skip(2).Any(t=>t.Accepts(item));
        void Color(ItemSlot slot,bool compatible)
        {
            string? color=compatible?"#5fbed4":null;
            if(slot.HexBackgroundColor!=color)slot.HexBackgroundColor=color;
        }
        for(int i=0;i<equipment.Count;i++)Color(equipment[i],item is not null&&i+2<targets.Count&&targets[i+2].Accepts(item));
        Color(hotbar[11],item is not null&&!clothing);
        for(int i=0;i<10;i++)Color(hotbar[i],item is not null&&!clothing&&i==selectedHotbar);
    }
    private void EquipmentClick(int slot,EnumMouseButton button)
    {
        if(slot<0||slot>=targets.Count)return;
        target=slot;
        if(button==EnumMouseButton.Right){Apply(slot,null);held=null;}
        else if(held is {} item){Apply(slot,item);held=null;}
        else Grab(targets[slot].Read());
    }
    private void RefreshEquipment()
    {
        renderFailed=false;
        for(int i=0;i<15;i++)
        {
            equipment[i].Itemstack=i+2<targets.Count?targets[i+2].Read()?.Clone():null;
            equipment[i].BackgroundIcon=i+2<targets.Count?targets[i+2].Icon:null;
            equipment.MarkSlotDirty(i);
        }
        hotbar[selectedHotbar].Itemstack=targets[0].Read()?.Clone();
        hotbar.MarkSlotDirty(selectedHotbar);
        hotbar[11].Itemstack=targets.Count>1?targets[1].Read()?.Clone():null;
        hotbar[11].BackgroundIcon="offhand";
        hotbar.MarkSlotDirty(11);
    }
    private int EquipmentAt(int x,int y)
    {
        foreach(var (grid,offset) in equipmentGrids){int slot=grid.At(x,y);if(slot>=0)return slot+offset;}
        return -1;
    }
    private void Apply(int slot,ItemStack? stack)
    {
        if(slot<0||slot>=targets.Count)return;
        if(stack is not null&&!targets[slot].Accepts(stack))return;
        pendingEquipment[slot]=stack?.Clone();
        if(equipmentTaskQueued)return;
        equipmentTaskQueued=true;
        capi.Event.EnqueueMainThreadTask(()=>
        {
            equipmentTaskQueued=false;
            var pending=pendingEquipment.ToArray();pendingEquipment.Clear();
            if(!IsOpened())return;
            using var scale=UseScale();
            foreach(var pair in pending)
            {
                try
                {
                    var current=targets[pair.Key].Read();
                    if(current is null&&pair.Value is null || current is not null&&pair.Value is not null&&current.ToBytes().AsSpan().SequenceEqual(pair.Value.ToBytes()))continue;
                    targets[pair.Key].Write(pair.Value);
                }
                catch(Exception e){failed(e);}
            }
            RefreshEquipment();
        },"vsdirector-actor-equipment");
    }
    public override void OnMouseDown(MouseEvent args)
    {
        using var scale=UseScale();
        orbitIdleSeconds=0;
        if(customizationTab!=0)
        {
            if(CustomizationDropdownOpen()){base.OnMouseDown(args);args.Handled=true;return;}
            if(previewBounds.PointInside(args.X,args.Y)) {rotating=true;orbitMouseX=args.X;args.Handled=true;return;}
            base.OnMouseDown(args);args.Handled=true;return;
        }
        if(held is null && args.Button is EnumMouseButton.Left or EnumMouseButton.Right
            && !ContainsWindow(args.X,args.Y) && CanOrbitWorld?.Invoke(args.X,args.Y)==true)
        {orbitingWorld=true;worldMouseX=args.X;worldMouseY=args.Y;args.Handled=true;return;}
        // A plain right-click cancels our private cursor before any slot can
        // clear equipment. It never drops an item into the real world.
        if(held is not null&&args.Button==EnumMouseButton.Right)
        {held=null;UpdateCompatibleSlots();args.Handled=true;return;}
        if(held is not null&&args.Button==EnumMouseButton.Left&&previewBounds.PointInside(args.X,args.Y))
        {QuickEquip(held);args.Handled=true;return;}
        if(held is null&&(args.Button is EnumMouseButton.Left or EnumMouseButton.Right)&&previewBounds.PointInside(args.X,args.Y))
        {
            rotating=true;orbitMouseX=args.X;
            args.Handled=true;return;
        }
        if(held is not null&&args.Button==EnumMouseButton.Left&&!ContainsWindow(args.X,args.Y))
        {held=null;args.Handled=true;return;}
        base.OnMouseDown(args);
        args.Handled=true;
    }
    public override void OnMouseUp(MouseEvent args)
    {
        using var scale=UseScale();
        if(customizationTab!=0){rotating=false;base.OnMouseUp(args);args.Handled=true;return;}
        if(orbitingWorld){orbitingWorld=false;args.Handled=true;return;}
        if(rotating){rotating=false;orbitIdleSeconds=0;args.Handled=true;return;}
        if(held is {} stack&&Math.Abs(args.X-pressX)+Math.Abs(args.Y-pressY)>6)
        {
            int slot=EquipmentAt(args.X,args.Y);
            int barSlot=hotbarGrid.At(args.X,args.Y);
            if(slot<0&&previewBounds.PointInside(args.X,args.Y))
                slot=Enumerable.Range(2,Math.Max(0,targets.Count-2)).FirstOrDefault(i=>targets[i].Accepts(stack),target);
            if(barSlot>=0){selectedHotbar=barSlot;hotbar[barSlot].Itemstack=stack.Clone();hotbar.MarkSlotDirty(barSlot);hotbarGrid.HighlightSlot(barSlot);slot=0;}
            if(slot>=0){Apply(slot,stack);held=null;}
            else if(catalogGrid.At(args.X,args.Y)>=0||!ContainsWindow(args.X,args.Y))held=null;
        }
        rotating=false;base.OnMouseUp(args);args.Handled=true;
    }
    public override void OnMouseMove(MouseEvent args)
    {
        using var scale=UseScale();
        if(orbitingWorld)
        {
            OrbitWorld?.Invoke((args.X-worldMouseX)/windowScale,(args.Y-worldMouseY)/windowScale);
            worldMouseX=args.X;worldMouseY=args.Y;args.Handled=true;return;
        }
        if(rotating)
        {
            orbitIdleSeconds=0;
            // Ungrabbed native UI events may have zero DeltaX/Y. Use the actual
            // pointer position and keep the gesture away from title-bar dragging.
            actorYaw=MathF.IEEERemainder(actorYaw-(args.X-orbitMouseX)*.01f/windowScale,MathF.Tau);
            orbitMouseX=args.X;
        }
        else base.OnMouseMove(args);
        UpdateCompatibleSlots();args.Handled=true;
    }
    public override void OnMouseWheel(MouseWheelEventArgs args)
    {
        using var scale=UseScale();
        if(customizationTab!=0 && (CustomizationDropdownOpen() || !previewBounds.PointInside(capi.Input.MouseX,capi.Input.MouseY)))
        {Composers["customization"].OnMouseWheel(args);args.SetHandled(true);return;}
        if(previewBounds.PointInside(capi.Input.MouseX,capi.Input.MouseY))
        {
            orbitIdleSeconds=0;
            float delta=args.deltaPrecise!=0?args.deltaPrecise:args.delta;
            actorZoom=Math.Clamp(actorZoom*MathF.Exp(delta*.15f),.35f,4f);
        }
        else if(ContainsNativeWindow(Composers["creative"],capi.Input.MouseX,capi.Input.MouseY))Composers["creative"].OnMouseWheel(args);
        else if(!ContainsWindow(capi.Input.MouseX,capi.Input.MouseY))
            ZoomWorld?.Invoke(args.deltaPrecise!=0?args.deltaPrecise:args.delta);
        args.SetHandled(true);
    }
    public override void OnRenderGUI(float deltaTime)
    {
        using var scale=UseScale();
        AdvanceOrbit(deltaTime);
        UpdateCompatibleSlots();
        base.OnRenderGUI(deltaTime);
        RenderActor(deltaTime);
        if(held is {} stack)capi.Render.RenderItemstackToGui(new DummySlot(stack),capi.Input.MouseX,capi.Input.MouseY,200,(float)GuiElement.scaled(32),-1,deltaTime,showStackSize:false);
    }
    private void AdvanceOrbit(float dt)
    {
        if(rotating||held is not null){orbitIdleSeconds=0;return;}
        orbitIdleSeconds+=Math.Clamp(dt,0,.1f);
        if(orbitIdleSeconds>2)actorYaw=MathF.IEEERemainder(actorYaw+Math.Clamp(dt,0,.1f)*.22f,MathF.Tau);
    }
    private void RenderActor(float dt)
    {
        if(renderFailed)return;
        try {renderActor(dt,previewBounds,actorYaw,actorPitch,actorZoom);}
        catch(Exception e){renderFailed=true;failed(e);}
    }
    public override void OnGuiOpened(){actorYaw=-MathF.PI/2;actorPitch=0;actorZoom=1;rotating=false;orbitIdleSeconds=0;base.OnGuiOpened();}
    public override void OnKeyDown(KeyEvent args)
    {
        using var scale=UseScale();
        if(args.KeyCode==(int)GlKeys.Escape)
        {
            if(held is not null){held=null;UpdateCompatibleSlots();}
            else TryClose();
            args.Handled=true;return;
        }
        base.OnKeyDown(args);
    }
    public override void OnKeyPress(KeyEvent args){using var scale=UseScale();base.OnKeyPress(args);}
    public override void OnGuiClosed()
    {
        orbitingWorld=false;pendingEquipment.Clear();
        held=null;rotating=false;orbitIdleSeconds=0;
        foreach(var composer in Composers.Values)
            foreach(var grid in Elements(composer,"interactiveElements").Values.OfType<ActorGrid>())
            {
                if(grid.hoverSlotId>=0&&grid.renderedSlots.TryGetValue(grid.hoverSlotId,out var slot))capi.Input.TriggerOnMouseLeaveSlot(slot);
                grid.hoverSlotId=-1;
            }
        base.OnGuiClosed();
    }
    public override void Dispose()
    {
        held=null;characterBuilder?.Dispose();
        customization?.Composers.Remove("createcharacter");customization?.Dispose();
        // Only our composers own the native builder's textures. Do not call the
        // unused window's Dispose/Close methods (they address live inventories).
        base.Dispose();
    }
}

[HarmonyPatch(typeof(GuiComposer),nameof(GuiComposer.Compose),[typeof(bool)])]
internal static class DirectorActorNativeComposePatch
{
    private static void Prefix(GuiComposer __instance)
        =>DirectorActorItemDialog.ComposingNative?.PrepareNativeCreative(__instance);
}

// Native composer names are globally cached. Give the builder its own cache keys
// so composing the actor cannot dispose the player's already-created windows.
[HarmonyPatch("Vintagestory.Client.NoObf.GuiComposerManager","Create")]
internal static class DirectorActorNativeComposerNamePatch
{
    private static void Prefix(ref string dialogName)
    {
        if(DirectorActorItemDialog.ComposingNative is not null)dialogName="vsdirector-actor-native-"+dialogName;
    }
}
