using System.Numerics;
using System.Text.Json.Nodes;
using ImGuiNET;
using VSDirector.Data;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private static string P(string key)=>L("creation-p3-"+key);
    private string particlePage="common",particleParameterSearch="";
    private readonly HashSet<Guid> particleMulti=[];
    private Guid particleTreeOwner,particleSolo;
    private float particleTreeRatio=.4f;
    private string particleTreeSearch="";
    private readonly Dictionary<Guid,Guid> particleNodeMemory=new();
    private readonly HashSet<string> particlePinned=[];
    private int particleFieldFilter;
    private Guid particleDragging;
    private unsafe void ParticleDrop(CreationParticleEffect effect,CreationParticleEmitter? parent)
    {
        if(!ImGui.BeginDragDropTarget())return;
        var payload=ImGui.AcceptDragDropPayload("DIRECTOR_PARTICLE_NODE");
        if(payload.NativePtr!=null && effect.AllNodes().FirstOrDefault(n=>n.Id==particleDragging) is {} moving && (parent is null || !moving.Descendants().Contains(parent)))
            Run(()=>Session.Edit(P("move-node"),_=>
            {
                var old=effect.Emitters.Contains(moving)?effect.Emitters:effect.AllNodes().First(n=>n.Children.Contains(moving)).Children;
                old.Remove(moving);(parent?.Children??effect.Emitters).Add(moving);
                moving.Trigger=parent is null?ParticleTrigger.Root:moving.Trigger==ParticleTrigger.Root?ParticleTrigger.Periodic:moving.Trigger;
                effect.Validate();
            }));
        ImGui.EndDragDropTarget();
    }
    private CreationParticleEmitter? particleClipboard;
    private void DrawParticleBrowser()
    {
        if(SelectedObject?.ParticleEffect is not {} effect){particleSolo=Guid.Empty;Runtime.ParticleSolo=Guid.Empty;DrawBrowser();return;}
        if(particleTreeOwner!=SelectedObject.Id){if(particleTreeOwner!=Guid.Empty)particleNodeMemory[particleTreeOwner]=particleEmitter;particleTreeOwner=SelectedObject.Id;particleEmitter=particleNodeMemory.GetValueOrDefault(particleTreeOwner);particleMulti.Clear();particleSolo=Guid.Empty;Runtime.ParticleSolo=Guid.Empty;}
        float available=ImGui.GetContentRegionAvail().Y;
        ImGui.BeginChild("particle-objects",new(0,Math.Max(60,available*particleTreeRatio)),false);DrawBrowser();ImGui.EndChild();
        ImGui.InvisibleButton("particle-split",new(-1,6*DirectorImGuiTheme.Scale));
        if(ImGui.IsItemHovered()||ImGui.IsItemActive())ImGui.SetMouseCursor(ImGuiMouseCursor.ResizeNS);
        if(ImGui.IsItemActive())particleTreeRatio=Math.Clamp(particleTreeRatio+ImGui.GetIO().MouseDelta.Y/Math.Max(1,available),.15f,.75f);
        ImGui.BeginChild("particle-hierarchy",new(0,0),false);
        ImGui.Selectable(P("hierarchy"),false);ParticleDrop(effect,null);
        bool first=true;
        if(InlineChoice(P("add"),false,ref first))Session.Edit(P("add"),_=>{var e=new CreationParticleEmitter{Modules=effect.Version>=3?new():null};effect.Emitters.Add(e);particleEmitter=e.Id;});
        if(InlineChoice(P("resources"),false,ref first))OpenParticlePicker();
        if(InlineChoice(P("files"),particlePage=="files",ref first))particlePage="files";
        ImGui.SetNextItemWidth(-1);ImGui.InputTextWithHint("##node-search",P("node-search"),ref particleTreeSearch,128);
        foreach(var root in effect.Emitters.ToArray())DrawParticleNode(effect,root,effect.Emitters,0);
        ImGui.EndChild();
    }
    private unsafe void DrawParticleNode(CreationParticleEffect effect,CreationParticleEmitter node,List<CreationParticleEmitter> siblings,int depth)
    {
        if(particleTreeSearch.Length>0&&!node.Descendants().Any(e=>e.Name.Contains(particleTreeSearch,StringComparison.OrdinalIgnoreCase)))return;
        ImGui.PushID(node.Id.ToString());
        bool enabled=node.Enabled;
        if(ImGui.Checkbox("##enabled",ref enabled))Session.Edit(L("creation-enabled"),_=>node.Enabled=enabled);
        ImGui.SameLine();
        var flags=ImGuiTreeNodeFlags.OpenOnArrow|ImGuiTreeNodeFlags.OpenOnDoubleClick|ImGuiTreeNodeFlags.SpanAvailWidth|ImGuiTreeNodeFlags.DefaultOpen;
        if(node.Id==particleEmitter||particleMulti.Contains(node.Id))flags|=ImGuiTreeNodeFlags.Selected;
        if(node.Children.Count==0)flags|=ImGuiTreeNodeFlags.Leaf;
        bool open=ImGui.TreeNodeEx(node.Name+"###node",flags);
        if(ImGui.IsItemClicked()&&!ImGui.IsItemToggledOpen())
        {
            if(!ImGui.GetIO().KeyCtrl)particleMulti.Clear();else{if(particleEmitter!=Guid.Empty)particleMulti.Add(particleEmitter);if(!particleMulti.Add(node.Id))particleMulti.Remove(node.Id);}
            particleEmitter=node.Id;particlesPanel=true;focusParticles=true;
        }
        if(ImGui.IsItemHovered())ImGui.SetTooltip(P("trigger-"+node.Trigger)+" / "+node.Source);
        if(ImGui.BeginDragDropSource())
        {particleDragging=node.Id;int token=1;ImGui.SetDragDropPayload("DIRECTOR_PARTICLE_NODE",(IntPtr)(&token),sizeof(int));ImGui.TextUnformatted(node.Name);ImGui.EndDragDropSource();}
        ParticleDrop(effect,node);
        if(ImGui.BeginPopupContextItem("node-menu"))
        {
            if(ImGui.MenuItem(P("copy")))particleClipboard=node.Duplicate();
            if(ImGui.MenuItem(P("paste"),"",false,particleClipboard is not null))Session.Edit(P("paste"),_=>siblings.Add(particleClipboard!.Duplicate()));
            if(ImGui.MenuItem(L("creation-duplicate")))Session.Edit(L("creation-duplicate"),_=>siblings.Add(node.Duplicate()));
            if(ImGui.MenuItem(P("rename")))Ask(P("rename"),name=>Session.Edit(P("rename"),_=>node.Name=name),node.Name);
            if(ImGui.MenuItem(P("move-up"),"",false,siblings.IndexOf(node)>0))Session.Edit(P("move-up"),_=>{int i=siblings.IndexOf(node);siblings.RemoveAt(i);siblings.Insert(i-1,node);});
            if(ImGui.MenuItem(P("move-down"),"",false,siblings.IndexOf(node)<siblings.Count-1))Session.Edit(P("move-down"),_=>{int i=siblings.IndexOf(node);siblings.RemoveAt(i);siblings.Insert(i+1,node);});
            if(ImGui.MenuItem(P("solo"),"",particleSolo==node.Id))
            {particleSolo=particleSolo==node.Id?Guid.Empty:node.Id;Runtime.ParticleSolo=particleSolo;Runtime.InvalidateParticleEnvironment();}
            if(ImGui.MenuItem(L("creation-delete")))Session.Edit(L("creation-delete"),_=>siblings.Remove(node));
            ImGui.EndPopup();
        }
        if(open){
            if(node.Id==particleEmitter)
            {
                foreach(string page in new[]{"common","emission","motion","appearance","environment","children","shape","schedule","forces","material","geometry","drivers"})
                {
                    bool module=page is "shape" or "schedule" or "forces" or "material" or "geometry" or "drivers";
                    if(module&&effect.Version<3)continue;
                    if(ImGui.Selectable(P(page)+"##section",module?particlePage=="modules"&&particleModulePage==page:particlePage==page))
                    {particlePage=module?"modules":page;if(module)particleModulePage=page;focusParticles=true;}
                }
            }
            foreach(var child in node.Children.ToArray())DrawParticleNode(effect,child,node.Children,depth+1);ImGui.TreePop();}
        ImGui.PopID();
    }
    private void DrawParticleChildActions(CreationParticleEffect effect,CreationParticleEmitter emitter)
    {
        if(effect.Version<3){Hint(P("upgrade-hint"));return;}
        if(effect.Emitters.Contains(emitter))Hint(P("trigger-Root"));
        else if(Combo(P("trigger"),P("trigger-"+emitter.Trigger)))
        {foreach(var trigger in Enum.GetValues<ParticleTrigger>().Where(t=>t!=ParticleTrigger.Root))if(ImGui.Selectable(P("trigger-"+trigger),emitter.Trigger==trigger))Session.Edit(P("trigger"),_=>emitter.Trigger=trigger);ImGui.EndCombo();}
        foreach(var trigger in new[]{ParticleTrigger.Birth,ParticleTrigger.Periodic,ParticleTrigger.Collision,ParticleTrigger.Death})
            if(Button(P("add")+" "+P("trigger-"+trigger)))Session.Edit(P("add"),_=>
            {var child=new CreationParticleEmitter{Trigger=trigger,Name=P("trigger-"+trigger),Modules=new()};child.Properties["SecondarySpawnInterval"]=CreationParticleFields.Nat(.2);emitter.Children.Add(child);particleEmitter=child.Id;});
        if(emitter.Modules is {} m)
        {
            Number(P("probability"),m.Probability,v=>m.Probability=v,0,1,.01f);
            Number(P("delay"),m.Delay,v=>m.Delay=v,0,86400,.01f);
            Number(P("inherit"),m.InheritVelocity,v=>m.InheritVelocity=v,0,100,.01f);
        }
        Hint(P("child-tree-hint"));
    }
    private void DrawParticleColor(CreationParticleEffect effect,CreationParticleEmitter emitter)
    {
        var array=CreationParticleFields.Find(emitter.Properties,"HsvaColor") as JsonArray;
        double Read(int i,double fallback)=>array?[i] is JsonObject n?CreationParticleFields.Number(n,"avg",fallback):fallback;
        var hsv=new Vector4((float)Read(0,0)/255,(float)Read(1,0)/255,(float)Read(2,255)/255,(float)Read(3,255)/255);
        bool changed=ImGui.ColorEdit4(P("color"),ref hsv,ImGuiColorEditFlags.InputHSV|ImGuiColorEditFlags.DisplayHSV|ImGuiColorEditFlags.AlphaBar);
        Widget(P("color"),changed,()=>
        {
            var copy=array is null?new JsonArray(CreationParticleFields.Nat(0),CreationParticleFields.Nat(0),CreationParticleFields.Nat(255),CreationParticleFields.Nat(255)):(JsonArray)array.DeepClone();
            for(int i=0;i<4;i++)CreationParticleFields.Set((JsonObject)copy[i]!,"avg",JsonValue.Create(hsv[i]*255));
            SetParticleNode(effect,emitter,["HsvaColor"],copy);
        });
    }
    private void DrawParticleFlags(CreationParticleEffect effect,CreationParticleEmitter emitter)
    {
        int bits=(int)CreationParticleFields.Number(emitter.Properties,"VertexFlags");
        var flags=new Vintagestory.API.Common.VertexFlags(bits);
        void Set()=>SetParticleNode(effect,emitter,["VertexFlags"],JsonValue.Create(flags.All));
        Number(P("glow"),bits&255,v=>SetParticleNode(effect,emitter,["VertexFlags"],JsonValue.Create((bits&~255)|(int)v)),0,255,1);
        Toggle(P("Reflective"),flags.Reflective,v=>{flags.Reflective=v;Set();});
        Number(P("ZOffset"),flags.ZOffset,v=>{flags.ZOffset=(byte)v;Set();},0,7,1);
        Toggle("LOD 0",flags.Lod0,v=>{flags.Lod0=v;Set();});
        Choice(P("WindMode"),flags.WindMode,v=>{flags.WindMode=v;Set();},Enum.GetNames<Vintagestory.API.Common.EnumWindBitMode>());
        Number(P("WindData"),flags.WindData,v=>{flags.WindData=(byte)v;Set();},0,7,1);
        Number(P("Normal"),flags.Normal,v=>{flags.Normal=(short)v;Set();},short.MinValue,short.MaxValue,1);
        Number("VertexFlags",bits,v=>SetParticleNode(effect,emitter,["VertexFlags"],JsonValue.Create((int)v)),0,int.MaxValue,1);
        foreach(string warning in CreationParticleFields.Warnings(emitter.Properties))Hint(warning);
    }
}
