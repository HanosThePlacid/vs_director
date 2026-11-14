using System.Reflection;
using HarmonyLib;
using System.Runtime.CompilerServices;
using System.Text;
using ImGuiNET;
using Vintagestory.Client.NoObf;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.GameContent;
using Vintagestory.API.MathTools;

internal static partial class DirectorImGuiProbe
{
    private sealed class ActorProbePlayer : EntityPlayer { public override string GetName()=>"Actual player"; }
    private sealed class ActorCatalogItem : Item
    {
        public override string GetHeldItemName(ItemStack stack)=>Code.Path;
        public override void GetHeldItemInfo(ItemSlot slot,StringBuilder text,IWorldAccessor world,bool debug) { }
    }
    private static void CheckActorDialog(Assembly assembly)
    {
        var type=assembly.GetType("VSDirector.Gui.DirectorActorItemDialog",true)!;
        var targetType=assembly.GetType("VSDirector.Gui.ActorEquipmentTarget",true)!;
        var targets=Array.CreateInstance(targetType,17);
        var equipment=new ItemStack?[17];int writes=0;
        for(int i=0;i<17;i++)
        {
            int slot=i;
            targets.SetValue(Activator.CreateInstance(targetType,
                "Slot "+i,(Func<ItemStack?>)(()=>equipment[slot]),(System.Func<ItemStack,bool>)(stack=>slot<2||slot==2&&stack.Collectible.Code.Path=="hat"),
                (Action<ItemStack?>)(stack=>{equipment[slot]=stack?.Clone();writes++;}),"shirt"),i);
        }
        var catalog=new[]{new ItemStack(new ActorCatalogItem{ItemId=1,Code=new("game:hat"),CreativeInventoryTabs=["general","clothing"]}),new ItemStack(new ActorCatalogItem{ItemId=2,Code=new("game:torch"),CreativeInventoryTabs=["general"]})};
        float saved=RuntimeEnv.GUIScale;
        try
        {
            foreach(float scale in new[]{1f,1.5f,2f})
            foreach(var screen in new[]{(1280,720),(1920,1080)})
            {
                RuntimeEnv.GUIScale=scale;
                ICoreClientAPI api=null!;
                int mouseX=0,mouseY=0;
                var keys=new bool[256];
                var rawKeys=new bool[256];
                var input=ProbeDispatchProxy.Create<IInputAPI>((m,_)=>m.Name switch {"get_KeyboardKeyState"=>keys,"get_KeyboardKeyStateRaw"=>rawKeys,"get_MouseX"=>mouseX,"get_MouseY"=>mouseY,_=>ProbeDispatchProxy.DefaultValue(m.ReturnType)});
                object[]? nativeDraw=null;
                int matrices=0,scissors=0;float drawnPitch=0;
                var render=ProbeDispatchProxy.Create<IRenderAPI>((m,_)=>
                {
                    if(m.Name=="GlPushMatrix")matrices++;
                    if(m.Name=="GlPopMatrix")matrices--;
                    if(m.Name=="PushScissor")scissors++;
                    if(m.Name=="PopScissor")scissors--;
                    if(m.Name=="GlRotate")drawnPitch=(float)_![0]!;
                    if(m.Name=="RenderEntityToGui"){nativeDraw=_!;return null;}
                    if(m.Name=="Render2DTexture")throw new Exception("Actor still uses a flat preview texture");
                    return m.Name switch
                {"get_FrameWidth"=>screen.Item1,"get_FrameHeight"=>screen.Item2,
                    "BitmapCreateFromPng"=>new BitmapExternal(new MemoryStream((byte[])_![0]!),api.Logger),
                    _=>ProbeDispatchProxy.DefaultValue(m.ReturnType)};
                });
                bool deferEquipment=false;var equipmentTasks=new Queue<Action>();
                var events=ProbeDispatchProxy.Create<IClientEventAPI>((m,a)=>{if(m.Name=="EnqueueMainThreadTask") {if(deferEquipment)equipmentTasks.Enqueue((Action)a![0]!);else ((Action)a![0]!)();}return ProbeDispatchProxy.DefaultValue(m.ReturnType);});
                var logger=ProbeDispatchProxy.Create<ILogger>((m,a)=>{if(m.Name=="Error")throw new Exception("Native dialog error: "+string.Join(" ",a??[]));return ProbeDispatchProxy.DefaultValue(m.ReturnType);});
                var network=ProbeDispatchProxy.Create<IClientNetworkAPI>((m,_)=>throw new Exception("Actor editor touched live network: "+m.Name));
                var registry=ProbeDispatchProxy.Create<IClassRegistryAPI>((m,_)=>m.Name=="CreateInvNetworkUtil"
                    ?ProbeDispatchProxy.Create<IInventoryNetworkUtil>((method,_)=>ProbeDispatchProxy.DefaultValue(method.ReturnType))
                    :ProbeDispatchProxy.DefaultValue(m.ReturnType));
                var assets=ProbeDispatchProxy.Create<IAssetManager>((m,a)=>
                {
                    if(m.Name is not ("Get" or "TryGet"))return ProbeDispatchProxy.DefaultValue(m.ReturnType);
                    var location=a![0] is AssetLocation loc?loc:new AssetLocation((string)a[0]!);
                    string root=Path.Combine(Environment.GetEnvironmentVariable("VINTAGE_STORY")??Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,"../../../../../../Vintagestory")),"assets");
                    string file=new[]{location.Domain,"survival","creative"}.Select(domain=>Path.Combine(root,domain,location.Path)).First(File.Exists);
                    return new Vintagestory.Common.Asset(location){Data=File.ReadAllBytes(file)};
                });
                int textures=0;
                GuiComposerManager nativeComposers=null!;
                var gui=ProbeDispatchProxy.Create<IGuiAPI>((m,a)=>
                {
                    if(m.Name=="get_Icons")return new IconUtil(api);
                    if(m.Name=="get_Text")return new TextDrawUtil();
                    if(m.Name=="get_TextTexture")return new TextTextureUtil(api);
                    if(m.Name=="CreateCompo")
                    {
                        var bounds=(ElementBounds)a![1]!;
                        bounds.ParentBounds=ElementBounds.Fixed(0,0,screen.Item1/RuntimeEnv.GUIScale,screen.Item2/RuntimeEnv.GUIScale).WithEmptyParent();
                        bounds.ParentBounds.CalcWorldBounds();
                        return nativeComposers.Create((string)a[0]!,bounds);
                    }
                    if(m.Name is "LoadCairoTexture" or "LoadOrUpdateCairoTexture")
                    {
                        var surface=(Cairo.ImageSurface)a![0]!;
                        if(surface.Width<=0||surface.Height<=0)throw new Exception("Zero native image surface");
                        textures++;
                        if(m.Name=="LoadCairoTexture")return textures;
                        var texture=(LoadedTexture)a[2]!;texture.Width=surface.Width;texture.Height=surface.Height;
                    }
                    return ProbeDispatchProxy.DefaultValue(m.ReturnType);
                });
                var liveHotbar=new InventoryGeneric(12,"probe-hotbar",null!);
                liveHotbar[1].Itemstack=catalog[1].Clone();
                liveHotbar[1].Itemstack!.Attributes.SetString("variant","hotbar-copy");
                var player=(ClientPlayer)RuntimeHelpers.GetUninitializedObject(typeof(ClientPlayer));
                var data=(ClientWorldPlayerData)RuntimeHelpers.GetUninitializedObject(typeof(ClientWorldPlayerData));
                data.PlayerUID="actorprobe";data.PlayerName="Actual player";data.CurrentGameMode=EnumGameMode.Survival;
                AccessTools.Field(typeof(ClientWorldPlayerData),"entityplayer").SetValue(data,new ActorProbePlayer());
                AccessTools.Field(typeof(ClientPlayer),"worlddata").SetValue(player,data);
                var manager=new ClientPlayerInventoryManager(new Vintagestory.API.Datastructures.OrderedDictionary<string,InventoryBase>
                {{"hotbar-actorprobe",liveHotbar},{"character-actorprobe",new InventoryGeneric(15,"character-actorprobe",null!)}},player,null!);
                AccessTools.Field(typeof(ClientPlayer),"inventoryMgr").SetValue(player,manager);
                var nativeApi=RuntimeHelpers.GetUninitializedObject(typeof(ClientCoreAPI));
                var world=ProbeDispatchProxy.Create<IClientWorldAccessor>((m,_)=>m.Name switch
                {"get_Player"=>player,"get_Items"=>catalog.Select(s=>(Item)s.Collectible).ToList(),"get_Blocks"=>new List<Block>(),"get_Api"=>nativeApi,"get_Side"=>EnumAppSide.Client,_=>ProbeDispatchProxy.DefaultValue(m.ReturnType)});
                var classes=new CharacterSystem {characterClasses=[new(){Code="commoner",Traits=[],Gear=[]},new(){Code="hunter",Traits=[],Gear=[]}],TraitsByCode=new()};
                var loader=ProbeDispatchProxy.Create<IModLoader>((m,_)=>m.Name=="GetModSystem"?classes:ProbeDispatchProxy.DefaultValue(m.ReturnType));
                api=ProbeDispatchProxy.Create<ICoreClientAPI>((m,_)=>m.Name switch
                {"get_Network"=>network,"get_World"=>world,"get_Gui"=>gui,"get_Render"=>render,"get_Input"=>input,"get_Event"=>events,"get_Logger"=>logger,"get_ClassRegistry"=>registry,"get_Assets"=>assets,"get_ModLoader"=>loader,
                    _=>ProbeDispatchProxy.DefaultValue(m.ReturnType)});
                nativeComposers=new GuiComposerManager(api);
                var playerWindow=nativeComposers.Create("playercharacter",ElementBounds.Fixed(0,0,100,100).WithEmptyParent());
                var playerInventory=nativeComposers.Create("inventory-creative",ElementBounds.Fixed(0,0,100,100).WithEmptyParent());
                Vintagestory.Common.InventoryPlayerCreative? liveCreative=null;
                if(scale!=1.5f)
                {
                    liveCreative=new("creative-actorprobe",api);
                    AccessTools.Method(liveCreative.GetType(),"UpdateFromWorld").Invoke(liveCreative,[world]);
                    foreach(var tab in liveCreative.CreativeTabs.Tabs)tab.CreateSearchCache(world);
                    liveCreative.SetTab(1);manager.Inventories.Add("creative-actorprobe",liveCreative);
                }
                var previewEntity=new EntityAgent();
                AccessTools.Property(typeof(Entity),"Properties").SetValue(previewEntity,new EntityProperties{Client=new EntityClientProperties([],new()){Size=1,Shape=new CompositeShape()}});
                previewEntity.Pos.SetPos(30000000,32770,-1000);previewEntity.Pos.Yaw=2;previewEntity.Pos.Pitch=1;
                var draw3d=AccessTools.Method(assembly.GetType("VSDirector.Runtime.DirectorCreationRuntime",true),"RenderAppearanceEntity");
                using var dialog=(GuiDialog)Activator.CreateInstance(type,BindingFlags.Instance|BindingFlags.NonPublic,null,
                    [api,"Actor equipment",targets,0,(Action<float,ElementBounds,float,float,float>)((dt,b,yaw,pitch,zoom)=>draw3d.Invoke(null,[api,previewEntity,dt,b,yaw,pitch,zoom,1f,2f])),(Action<Exception>)(e=>throw new Exception("Actor write",e))],null)!;
                if(!ReferenceEquals(nativeComposers.Composers["playercharacter"],playerWindow)||!ReferenceEquals(nativeComposers.Composers["inventory-creative"],playerInventory))
                    throw new Exception("Actor native builders replaced player window cache");
                if(RuntimeEnv.GUIScale!=scale)throw new Exception("Actor window changed global GUI scale");
                foreach(var panel in dialog.Composers.Values)
                    if(panel.Bounds.renderX<0||panel.Bounds.renderY<0||panel.Bounds.renderX+panel.Bounds.OuterWidth>screen.Item1+1||panel.Bounds.renderY+panel.Bounds.OuterHeight>screen.Item2+1)
                        throw new Exception($"Native panel outside screen {scale} {screen} {panel.DialogName}: {panel.Bounds.renderX},{panel.Bounds.renderY},{panel.Bounds.OuterWidth},{panel.Bounds.OuterHeight}");
                var actorBounds=(ElementBounds)AccessTools.Field(type,"previewBounds").GetValue(dialog)!;
                var renderer=(EntityShapeRenderer)RuntimeHelpers.GetUninitializedObject(typeof(EntityShapeRenderer));
                AccessTools.Field(typeof(EntityShapeRenderer),"ModelMat").SetValue(renderer,new float[16]);
                if(Math.Abs((float)AccessTools.Field(type,"actorYaw").GetValue(dialog)!+MathF.PI/2)>.0001)throw new Exception("Actor did not open facing front");
                foreach(float angle in new[]{0f,1f,2f,3.14f,4.7f})
                {
                    AccessTools.Field(type,"actorYaw").SetValue(dialog,angle);
                    AccessTools.Method(type,"RenderActor").Invoke(dialog,[.016f]);
                    if(nativeDraw is null||!ReferenceEquals(nativeDraw[1],previewEntity))throw new Exception("Actor GUI did not render the private native entity");
                    AccessTools.Method(typeof(EntityShapeRenderer),"loadModelMatrixForGui").Invoke(renderer,[previewEntity,nativeDraw[2],nativeDraw[3],nativeDraw[4],(double)(float)nativeDraw[5],nativeDraw[6]]);
                    var matrix=(float[])AccessTools.Field(typeof(EntityShapeRenderer),"ModelMat").GetValue(renderer)!;
                    var center=Mat4f.MulWithVec4(matrix,new float[]{.5f,1,.5f,1});
                    if(Math.Abs(center[0]-(actorBounds.renderX+actorBounds.InnerWidth/2))>.01||Math.Abs(center[1]-(actorBounds.renderY+actorBounds.InnerHeight/2))>.01)
                        throw new Exception("Native actor GUI is not centered after rotation/DPI scaling");
                }
                AccessTools.Field(typeof(GuiDialog),"opened").SetValue(dialog,true);
                dialog.OnGuiOpened();
                var owner=new VSDirector.VSDirectorModSystem();
                var runtime=RuntimeHelpers.GetUninitializedObject(assembly.GetType("VSDirector.Runtime.DirectorCreationRuntime",true)!);
                AccessTools.Field(runtime.GetType(),"nativeAppearanceDialog").SetValue(runtime,dialog);
                AccessTools.Field(owner.GetType(),"creation").SetValue(owner,runtime);
                var host=RuntimeHelpers.GetUninitializedObject(assembly.GetType("VSDirector.Gui.DirectorImGuiWorkspace",true)!);
                AccessTools.Field(host.GetType(),"creationPanels").SetValue(host,new List<Action>());
                var current=AccessTools.Field(runtime.GetType(),"<Current>k__BackingField");var previousCurrent=current.GetValue(null);
                int outlines=0,panels=0;
                AccessTools.Property(host.GetType(),"AppearanceOverlay").SetValue(host,(Action)(()=>
                {
                    outlines++;var list=ImGui.GetWindowDrawList();int before=list.VtxBuffer.Size;
                    list.AddRect(new(10,10),new(100,100),0xff70d1ff);
                    if(list.VtxBuffer.Size<=before)throw new Exception("Appearance overlay was clipped out");
                }));
                AccessTools.Method(host.GetType(),"AddCreation").Invoke(host,[(Action)(()=>panels++)]);
                try
                {
                    current.SetValue(null,runtime);
                    for(int frame=0;frame<2;frame++)
                    {
                        ImGui.NewFrame();AccessTools.Method(host.GetType(),"DrawCreationHost").Invoke(host,null);ImGui.Render();
                    }
                    if(outlines!=2||panels!=0||dialog.DrawOrder<=.85||dialog.DrawOrder>=.89)
                        throw new Exception("Native equipment mode skipped actor overlay or drew it above dialogs");
                }
                finally{current.SetValue(null,previousCurrent);}
                var hud=(GuiDialog)RuntimeHelpers.GetUninitializedObject(assembly.GetType("VSDirector.Gui.DirectorWorkspaceHud",true)!);
                var workspace=RuntimeHelpers.GetUninitializedObject(assembly.GetType("VSDirector.Gui.DirectorCreationWorkspace",true)!);
                AccessTools.Property(workspace.GetType(),"Active").SetValue(workspace,true);
                AccessTools.Field(hud.GetType(),"creationWorkspace").SetValue(hud,workspace);
                AccessTools.Field(hud.GetType(),"owner").SetValue(hud,owner);
                AccessTools.Field(hud.GetType(),"layout").SetValue(hud,Activator.CreateInstance(AccessTools.Field(hud.GetType(),"layout").FieldType));
                AccessTools.Field(typeof(GuiDialog),"opened").SetValue(hud,true);
                var game=(ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
                AccessTools.Field(typeof(ClientMain),"LoadedGuis").SetValue(game,new List<GuiDialog>{hud,dialog});
                AccessTools.Field(typeof(ClientMain),"OpenedGuis").SetValue(game,new List<GuiDialog>{hud,dialog});
                var router=(GuiManager)RuntimeHelpers.GetUninitializedObject(typeof(GuiManager));
                AccessTools.Field(typeof(GuiManager),"game").SetValue(router,game);
                AccessTools.Field(typeof(GuiManager),"ignoreFocusEvents").SetValue(router,true);
                if(hud.ShouldReceiveMouseEvents())throw new Exception("Workspace still intercepts native actor mouse input");
                mouseX=(int)(actorBounds.renderX+actorBounds.InnerWidth/2);mouseY=(int)(actorBounds.renderY+actorBounds.InnerHeight/2);
                router.OnMouseDown(new(mouseX,mouseY,EnumMouseButton.Left,0));
                mouseX+=40;mouseY+=30;
                router.OnMouseMove(new(mouseX,mouseY,0,0));
                router.OnMouseUp(new(mouseX,mouseY,EnumMouseButton.Left,0));
                AccessTools.Method(type,"RenderActor").Invoke(dialog,[.016f]);
                if(Math.Abs((float)nativeDraw![5]+MathF.PI/2)<.01||Math.Abs(drawnPitch)>.001)
                    throw new Exception("Actor drag must rotate horizontally without pitching");
                float horizontalYaw=(float)nativeDraw[5];
                router.OnMouseDown(new(mouseX,mouseY,EnumMouseButton.Left,0));
                mouseY+=25;router.OnMouseMove(new(mouseX,mouseY,0,0));
                router.OnMouseUp(new(mouseX,mouseY,EnumMouseButton.Left,0));
                AccessTools.Method(type,"RenderActor").Invoke(dialog,[.016f]);
                if((float)nativeDraw[5]!=horizontalYaw||Math.Abs(drawnPitch)>.001)throw new Exception("Vertical drag changed actor orientation");
                float originalSize=(float)nativeDraw[6];
                mouseX=(int)(actorBounds.renderX+actorBounds.InnerWidth/2);mouseY=(int)(actorBounds.renderY+actorBounds.InnerHeight/2);
                router.OnMouseWheel(new MouseWheelEventArgs{delta=1,deltaPrecise=1});
                AccessTools.Method(type,"RenderActor").Invoke(dialog,[.016f]);
                if((float)nativeDraw[6]<=originalSize||matrices!=0||scissors!=0)throw new Exception("Actor zoom or GL/scissor restoration failed");
                dialog.OnGuiOpened();
                if((float)AccessTools.Field(type,"actorZoom").GetValue(dialog)! !=1||(float)AccessTools.Field(type,"actorPitch").GetValue(dialog)! !=0)
                    throw new Exception("Reopening actor did not restore front view");
                float initialYaw=(float)AccessTools.Field(type,"actorYaw").GetValue(dialog)!;
                for(int i=0;i<40;i++)AccessTools.Method(type,"AdvanceOrbit").Invoke(dialog,[.1f]);
                if(Math.Abs((float)AccessTools.Field(type,"actorYaw").GetValue(dialog)!-initialYaw)<.05f)throw new Exception("Idle actor does not auto-rotate");
                var character=dialog.Composers["character"];
                var browser=dialog.Composers["creative"];
                var tabsBounds=browser.GetVerticalTab("verticalTabs").Bounds;
                if(character.Bounds.renderX+character.Bounds.OuterWidth>tabsBounds.renderX)
                    throw new Exception("Native character overlaps creative category tabs");
                if(browser.Bounds.renderY+browser.Bounds.OuterHeight>dialog.Composers["hotbar"].Bounds.renderY)
                    throw new Exception("Native creative window overlaps hotbar");
                if(character.GetSlotGrid("leftSlots") is null||character.GetElement("tabs") is null
                    ||browser.GetElement("verticalTabs") is null||browser.GetTextInput("searchbox") is null
                    ||browser.GetElement("page") is not null||browser.GetElement("search") is not null)
                    throw new Exception("Not the native character/creative composers");
                AccessTools.Property(type,"CanOrbitWorld").SetValue(dialog,(System.Func<int,int,bool>)((x,y)=>true));
                int accidentalOrbits=0;AccessTools.Property(type,"OrbitWorld").SetValue(dialog,(Action<float,float>)((x,y)=>accidentalOrbits++));
                void ClickCategory(int index)
                {
                    var tabs=browser.GetVerticalTab("verticalTabs");
                    var tabItems=(GuiTab[])AccessTools.Field(typeof(GuiElementVerticalTabs),"tabs").GetValue(tabs)!;
                    var widths=(int[])AccessTools.Field(typeof(GuiElementVerticalTabs),"tabWidths").GetValue(tabs)!;
                    double tabHeight=(double)AccessTools.Field(typeof(GuiElementVerticalTabs),"tabHeight").GetValue(tabs)!;
                    double uiScale=(float)AccessTools.Field(type,"windowScale").GetValue(dialog)!;
                    mouseX=(int)(tabs.Bounds.absX+tabs.Bounds.InnerWidth-widths[index]/2d);
                    mouseY=(int)(tabs.Bounds.absY+index*(tabHeight+5*uiScale)+tabItems.Take(index+1).Sum(t=>t.PaddingTop)+tabHeight/2);
                    router.OnMouseDown(new(mouseX,mouseY,EnumMouseButton.Left,0));
                    router.OnMouseMove(new(mouseX+1,mouseY+1,0,0));
                    router.OnMouseUp(new(mouseX,mouseY,EnumMouseButton.Left,0));
                    if(accidentalOrbits!=0||tabs.ActiveElement!=index)throw new Exception("Native side tab was captured by world orbit");
                }
                ClickCategory(1);
                if(browser.GetSlotGrid("slotgrid").renderedSlots.Count!=1)throw new Exception("Native category switch failed");
                ClickCategory(0);
                AccessTools.Property(type,"CanOrbitWorld").SetValue(dialog,null);
                if(liveCreative is not null&&liveCreative.CurrentTabIndex!=1)throw new Exception("Actor category changed player's selected tab");
                browser.GetTextInput("searchbox").SetValue("missing item");
                if(browser.GetSlotGrid("slotgrid").renderedSlots.Count!=0)throw new Exception("Native creative search did not filter in survival");
                browser.GetTextInput("searchbox").SetValue("hat");
                var grid=browser.GetSlotGrid("slotgrid");
                if(grid.renderedSlots.Count!=1)throw new Exception("Native creative search lost matching hat");
                AccessTools.Field(typeof(GuiDialog),"opened").SetValue(dialog,true);
                var source=grid.SlotBounds[0];mouseX=(int)(source.renderX+10);mouseY=(int)(source.renderY+10);
                AccessTools.Method(type,"UpdateCompatibleSlots").Invoke(dialog,null);
                var actorInventory=(InventoryBase)AccessTools.Field(type,"equipment").GetValue(dialog)!;
                if(actorInventory[0].HexBackgroundColor!="#5fbed4"||actorInventory[1].HexBackgroundColor is not null)
                    throw new Exception("Native compatible clothing slot highlight missing or overly broad");
                grid.OnMouseDownOnElement(api,new(mouseX,mouseY,EnumMouseButton.Left,0));
                dialog.OnMouseUp(new(mouseX,mouseY,EnumMouseButton.Left,0));
                var left=character.GetSlotGrid("leftSlots");var hat=left.SlotBounds[0];
                mouseX=(int)(hat.renderX+10);mouseY=(int)(hat.renderY+10);
                left.OnMouseDownOnElement(api,new(mouseX,mouseY,EnumMouseButton.Left,0));
                if(equipment[2]?.Collectible.Code.Path!="hat")throw new Exception("Native clothing slot failed to equip");
                left.OnMouseDownOnElement(api,new(mouseX,mouseY,EnumMouseButton.Right,0));
                if(equipment[2] is not null)throw new Exception("Native clothing slot failed to clear");
                foreach(var shift in new[]{GlKeys.ShiftLeft,GlKeys.ShiftRight})
                {
                    keys[(int)shift]=true;
                    grid.OnMouseDownOnElement(api,new((int)source.renderX+10,(int)source.renderY+10,EnumMouseButton.Right,0));
                    keys[(int)shift]=false;
                    if(equipment[2] is not null)throw new Exception("Removed Shift equip still active");
                    left.OnMouseDownOnElement(api,new(mouseX,mouseY,EnumMouseButton.Right,0));
                }
                mouseX=(int)source.renderX+10;mouseY=(int)source.renderY+10;
                foreach(bool modifierOnly in new[]{true,false})
                {
                    if(!modifierOnly)rawKeys[(int)GlKeys.ShiftRight]=true;
                    router.OnMouseDown(new(mouseX,mouseY,EnumMouseButton.Right,modifierOnly?(int)EnumModifierKey.SHIFT:0));
                    router.OnMouseUp(new(mouseX,mouseY,EnumMouseButton.Right,0));
                    rawKeys[(int)GlKeys.ShiftRight]=false;
                    if(equipment[2] is not null)throw new Exception("Removed routed Shift equip still active");
                    equipment[2]=null;
                }
                foreach(bool escape in new[]{false,true})
                {
                    router.OnMouseDown(new(mouseX,mouseY,EnumMouseButton.Left,0));
                    router.OnMouseUp(new(mouseX,mouseY,EnumMouseButton.Left,0));
                    if(AccessTools.Field(type,"held").GetValue(dialog) is null)throw new Exception("Routed catalog selection failed");
                    int before=writes;
                    if(escape)dialog.OnKeyDown(new KeyEvent{KeyCode=(int)GlKeys.Escape});
                    else router.OnMouseDown(new(mouseX,mouseY,EnumMouseButton.Right,0));
                    if(!dialog.IsOpened()||AccessTools.Field(type,"held").GetValue(dialog) is not null||writes!=before)
                        throw new Exception("Cursor cancellation closed editor or modified actor");
                }
                equipment[2]=null;deferEquipment=true;int writesBefore=writes;
                for(int i=0;i<3;i++)
                {
                    grid.SlotClick(api,grid.renderedSlots.GetKeyAtIndex(0),EnumMouseButton.Left,false,false,false);
                    left.SlotClick(api,0,EnumMouseButton.Left,false,false,false);
                }
                if(equipmentTasks.Count!=1)throw new Exception("Repeated equipment requests were not coalesced");
                while(equipmentTasks.TryDequeue(out var equip))equip();
                deferEquipment=false;
                if(writes!=writesBefore+1||equipment[2]?.Collectible.Code.Path!="hat")throw new Exception("Pending equipment request was not applied once");
                foreach(bool shiftFirst in new[]{true,false})
                {
                    equipment[2]=null;
                    if(shiftFirst)dialog.OnKeyDown(new KeyEvent{KeyCode=(int)GlKeys.ShiftLeft});
                    router.OnMouseDown(new(mouseX,mouseY,EnumMouseButton.Right,0));
                    if(!shiftFirst)dialog.OnKeyDown(new KeyEvent{KeyCode=(int)GlKeys.ShiftLeft});
                    router.OnMouseUp(new(mouseX,mouseY,EnumMouseButton.Right,0));
                    dialog.OnKeyUp(new KeyEvent{KeyCode=(int)GlKeys.ShiftLeft});
                    if(equipment[2] is not null)throw new Exception("Removed late Shift equip still active");
                }
                equipment[2]=null;
                grid.SlotClick(api,grid.renderedSlots.GetKeyAtIndex(0),EnumMouseButton.Right,true,false,false);
                if(equipment[2] is not null)throw new Exception("Removed slot Shift equip still active");
                var scroll=browser.GetScrollbar("scrollbar");float scrollBefore=scroll.CurrentYPosition;
                float zoomBefore=(float)AccessTools.Field(type,"actorZoom").GetValue(dialog)!;
                float worldWheel=0;AccessTools.Property(type,"ZoomWorld").SetValue(dialog,(Action<float>)(delta=>worldWheel+=delta));
                mouseX=0;mouseY=0;
                float orbitX=0,orbitY=0;
                AccessTools.Property(type,"CanOrbitWorld").SetValue(dialog,(System.Func<int,int,bool>)((x,y)=>x<50&&y<50));
                AccessTools.Property(type,"OrbitWorld").SetValue(dialog,(Action<float,float>)((x,y)=>{orbitX+=x;orbitY+=y;}));
                router.OnMouseDown(new(0,0,EnumMouseButton.Left,0));
                router.OnMouseMove(new(10,15,0,0));router.OnMouseUp(new(10,15,EnumMouseButton.Left,0));
                if(orbitX<=0||orbitY<=0)throw new Exception("Actor dialog swallowed world orbit drag");
                float stopped=orbitX;router.OnMouseMove(new(20,25,0,0));
                if(orbitX!=stopped)throw new Exception("World orbit remained captured after mouse release");
                router.OnMouseWheel(new MouseWheelEventArgs{delta=-1,deltaPrecise=-1});
                if(scroll.CurrentYPosition!=scrollBefore||(float)AccessTools.Field(type,"actorZoom").GetValue(dialog)! !=zoomBefore)
                    throw new Exception("Wheel outside panels changed creative scroll or actor zoom");
                if(worldWheel!=-1)throw new Exception("World wheel was swallowed by the actor dialog");
                mouseX=(int)source.renderX+10;mouseY=(int)source.renderY+10;
                router.OnMouseWheel(new MouseWheelEventArgs{delta=1,deltaPrecise=1});
                if(worldWheel!=-1||(float)AccessTools.Field(type,"actorZoom").GetValue(dialog)! !=zoomBefore)throw new Exception("Creative wheel leaked to actor/world zoom");
                mouseX=0;mouseY=0;
                AccessTools.Method(type,"UpdateCompatibleSlots").Invoke(dialog,null);
                if(actorInventory[0].HexBackgroundColor is not null)throw new Exception("Compatibility highlight persisted after leaving item");
                var hotbarGrid=dialog.Composers["hotbar"].GetSlotGrid("hotbargrid");
                var barSlot=hotbarGrid.SlotBounds[1];mouseX=(int)(barSlot.renderX+10);mouseY=(int)(barSlot.renderY+10);
                hotbarGrid.OnMouseDownOnElement(api,new(mouseX,mouseY,EnumMouseButton.Left,0));
                var offhand=dialog.Composers["hotbar"].GetSlotGrid("offhandgrid").SlotBounds[0];
                dialog.OnMouseUp(new((int)(offhand.renderX+10),(int)(offhand.renderY+10),EnumMouseButton.Left,0));
                if(equipment[0]?.Collectible.Code.Path!="torch")throw new Exception("Native hotbar selection did not update actor right hand");
                if(equipment[1]?.Collectible.Code.Path!="torch"||equipment[1]?.Attributes.GetString("variant")!="hotbar-copy")
                    throw new Exception("Native offhand drag lost item or attributes");
                equipment[1]!.Attributes.SetString("variant","actor-only");
                if(liveHotbar[1].Itemstack!.Attributes.GetString("variant")!="hotbar-copy"||liveHotbar[1].StackSize!=1)
                    throw new Exception("Actor edit mutated player inventory");
                previewEntity.World=world;previewEntity.Api=api;
                var skin=new EntityBehaviorExtraSkinnable(previewEntity);
                var part=new SkinnablePart{Code="voicetype",UseDropDown=true,Variants=[new(){Code="altoflute"},new(){Code="soprano"}]};
                part.VariantsByCode=part.Variants.ToDictionary(v=>v.Code);
                skin.AvailableSkinParts=[part];skin.AvailableSkinPartsByCode.Add(part.Code,part);
                AccessTools.Field(typeof(EntityBehaviorExtraSkinnable),"skintree").SetValue(skin,new TreeAttribute());
                previewEntity.Properties.Client.Behaviors.Add(skin);
                string? chosenSkin=null,chosenClass=null;
                byte[] liveBefore=player.Entity.WatchedAttributes.ToBytes();
                AccessTools.Method(type,"ConfigureCustomization").Invoke(dialog,[(Func<EntityAgent>)(()=>previewEntity),
                    (Action<string,string>)((part,variant)=>chosenSkin=part+":"+variant),(Action<string>)(code=>chosenClass=code)]);
                var tabs=character.GetHorizontalTabs("tabs");
                if(tabs.tabs.Length!=3)throw new Exception("Character is missing native customization tabs");
                AccessTools.Property(type,"CanOrbitWorld").SetValue(dialog,(System.Func<int,int,bool>)((x,y)=>true));
                void ClickActorTab(GuiElementHorizontalTabs tab,int index)
                {
                    var widths=(int[])AccessTools.Field(typeof(GuiElementHorizontalTabs),"tabWidths").GetValue(tab)!;
                    double uiScale=(float)AccessTools.Field(type,"windowScale").GetValue(dialog)!;
                    double offset=(double)AccessTools.Field(typeof(GuiElementHorizontalTabs),"currentScrollOffset").GetValue(tab)!;
                    mouseX=(int)(tab.Bounds.absX+(index+1)*tab.unscaledTabSpacing*uiScale+widths.Take(index).Sum()+widths[index]/2d-offset);
                    mouseY=(int)(tab.Bounds.absY+tab.Bounds.InnerHeight/2);
                    router.OnMouseDown(new(mouseX,mouseY,EnumMouseButton.Left,0));
                    router.OnMouseUp(new(mouseX,mouseY,EnumMouseButton.Left,0));
                }
                ClickActorTab(tabs,1);
                var custom=dialog.Composers["customization"];
                if(custom is null||character.Enabled||browser.Enabled||custom.GetDropDown("dropdown-voicetype") is null)
                    throw new Exception("Skin tab did not compose native skin/voice controls");
                var dropdown=custom.GetDropDown("dropdown-voicetype");
                ((SelectionChangedDelegate)AccessTools.Field(typeof(GuiElementDropDown),"onSelectionChanged").GetValue(dropdown)!)("soprano",true);
                if(chosenSkin!="voicetype:soprano")throw new Exception("Native skin controls did not edit actor");
                ClickActorTab(custom.GetHorizontalTabs("tabs"),2);
                custom=dialog.Composers["customization"];
                if(custom.GetDynamicText("className") is null||chosenClass is not null)throw new Exception("Opening native class tab changed actor class");
                var builder=AccessTools.Field(type,"customization").GetValue(dialog)!;
                AccessTools.Method(typeof(GuiDialogCreateCharacter),"changeClass").Invoke(builder,[1]);
                if(chosenClass!="hunter"||!liveBefore.AsSpan().SequenceEqual(player.Entity.WatchedAttributes.ToBytes()))
                    throw new Exception("Class editor failed isolation or selection");
                ClickActorTab(custom.GetHorizontalTabs("tabs"),0);
                if(!character.Enabled||!browser.Enabled||custom.Enabled)throw new Exception("Returning to equipment failed");
                dialog.TryClose();
                if(dialog.IsOpened()||AccessTools.Field(type,"held").GetValue(dialog) is not null)
                    throw new Exception("Native actor close kept cursor or open state");
                playerWindow.Dispose();playerInventory.Dispose();
                if(textures==0)throw new Exception("Native Cairo composition was not exercised");
            }
        }
        finally{RuntimeEnv.GUIScale=saved;}
        Console.WriteLine("PASS native actor/creative windows, inventory isolation, horizontal-only drag, preview/world/catalog wheel routing, queued equipment and removed Shift shortcut at 100/150/200%");
    }
}
