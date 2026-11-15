using System.Numerics;
using ImGuiNET;
using VSDirector.Data;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private string particleModulePage="shape",particleDriver="Size";
    private bool particleCurveOpen,particleCurveDragging;
    private int particleCurveSelection;
    private string particleExpressionDraft="",particleExpressionOwner="";
    private void DrawParticleModules(CreationSceneObject obj,CreationParticleEmitter emitter)
    {
        if(obj.ParticleEffect!.Version<3){Hint(P("upgrade-hint"));return;}
        if(particleModulePage=="geometry"&&!obj.ParticleEffect.Emitters.Contains(emitter)){Hint(P("geometry-root-only"));return;}
        var m=emitter.Modules!;
        bool first=true;
        foreach(string page in new[]{"shape","schedule","forces","material","geometry","drivers"})
            if(InlineChoice(P(page),particleModulePage==page,ref first))particleModulePage=page;
        void ForEach(Action<CreationParticleModules> edit){foreach(var node in ParticleTargets(obj.ParticleEffect,emitter))if(node.Modules is {} target)edit(target);}
        if(particleModulePage=="shape")
        {
            Choice(P("shape"),m.Shape,v=>ForEach(t=>t.Shape=v),Enum.GetValues<ParticleSpawnShape>().Select(t=>P(t.ToString())).ToArray());
            Choice(P("direction"),m.Direction,v=>ForEach(t=>t.Direction=v),Enum.GetValues<ParticleDirection>().Select(t=>P(t.ToString())).ToArray());
            ParticleAttachment(P("attachment"),m.BindObject,m.BindBone,(id,bone)=>ForEach(t=>{t.BindObject=id;t.BindBone=bone;}));
        }
        if(particleModulePage=="material")Choice(P("facing"),m.Facing,v=>ForEach(t=>t.Facing=v),Enum.GetValues<ParticleFacing>().Select(t=>P("facing-"+t)).ToArray());
        if(particleModulePage=="geometry")
        {
            Choice(P("geometry"),m.Geometry,v=>ForEach(t=>t.Geometry=v),Enum.GetValues<ParticleGeometry>().Select(t=>P(t.ToString())).ToArray());
            if(m.Geometry==ParticleGeometry.Trail&&Combo(P("trail-source"),obj.ParticleEffect.AllNodes().FirstOrDefault(e=>e.Id==m.TrailEmitter)?.Name??P("attachment")))
            {
                if(ImGui.Selectable(P("attachment")))Session.Edit(P("trail-source"),_=>m.TrailEmitter=Guid.Empty);
                foreach(var source in obj.ParticleEffect.AllNodes().Where(e=>e.Id!=emitter.Id&&e.Modules?.Geometry==ParticleGeometry.Particle))if(ImGui.Selectable(source.Name+"##"+source.Id))Session.Edit(P("trail-source"),_=>m.TrailEmitter=source.Id);
                ImGui.EndCombo();
            }
            ParticleAttachment(P("end-attachment"),m.EndObject,m.EndBone,(id,bone)=>ForEach(t=>{t.EndObject=id;t.EndBone=bone;}));
        }
        string[] fields=particleModulePage switch
        {
            "shape"=>["Extent","Surface","DirectionSpeed","MeshSource","MeshParticle"],
            "schedule"=>["LoopPeriod","DistanceInterval","Probability","Delay","InheritVelocity"],
            "forces"=>["Acceleration","Drag","ForceCenter","Attraction","Vortex","Noise","Parametric","InitialRotation","Spin","SpinAcceleration","Friction","KillPlane","KillHeight","ExpireBlocks","ExpireOutsideBlocks","ColorBlock"],
            "material"=>["Width","Height","UvOrigin","UvSize","Columns","Rows","Frames","FramesPerSecond","AnimateByLife","TextureLoop"],
            "geometry"=>["End","RibbonWidth","Retain","SampleDistance","Segments","Bend","Jitter","Branches","FlickerRate","TextureFlow","ClipAtTerrain","FadeEnds"],_=>[]
        };
        foreach(string field in fields)
        {
            var property=typeof(CreationParticleModules).GetProperty(field)!;object value=property.GetValue(m)!;
            void Set(object next)=>ForEach(t=>property.SetValue(t,next));
            string label=P(field);
            if(value is CreationVector vector)VectorField(label,vector,v=>Set(v),.01f);
            else if(value is bool b)Toggle(label,b,v=>Set(v));
            else if(value is string text)TextField(label,text,v=>Set(v));
            else if(value is int integer)Number(label,integer,v=>{Set((int)v);ForEach(t=>t.Frames=Math.Clamp(t.Frames,1,t.Columns*t.Rows));},field=="Branches"?0:field=="Segments"?2:1,field=="Branches"?16:field=="Segments"?512:field=="Frames"?m.Columns*m.Rows:256,1);
            else if(value is double number)
            {
                double min=field is "Attraction" or "Vortex" or "Spin" or "SpinAcceleration" or "InitialRotation" or "KillHeight" or "TextureFlow" or "Bend"?-1e6:field=="SampleDistance"?.001:0;
                Number(label,number,v=>Set(v),min,field is "Probability" or "Friction"?1:1e6,.01f);
            }
        }
        if(particleModulePage=="shape")
        {
            if(m.Shape==ParticleSpawnShape.Path)DrawParticlePath(m);
            if((m.Shape==ParticleSpawnShape.Mesh||m.MeshParticle)&&Button(P("capture-mesh")))Run(()=>Session.Edit(P("capture-mesh"),_=>m.Mesh=Runtime.ParticleMesh(m.MeshSource)));
        }
        if(particleModulePage=="schedule")
        {
            for(int i=0;i<m.Bursts.Count;i++){int index=i;ImGui.PushID(i);Number(P("burst-time"),m.Bursts[i],v=>m.Bursts[index]=v,0,86400);if(Button(L("creation-delete")))Session.Edit(P("burst-time"),_=>m.Bursts.RemoveAt(index));ImGui.PopID();}
            if(Button(P("add-burst")))Session.Edit(P("add-burst"),_=>m.Bursts.Add(m.Bursts.LastOrDefault()+.5));
        }
        if(particleModulePage=="geometry")
        {
            Hint(P("ribbon-profile"));
            for(int i=0;i<m.RibbonProfile.Points.Count;i++)
            {int index=i;ImGui.PushID("ribbon-width-"+i);Number(P("age"),m.RibbonProfile.Points[i].Time,v=>m.RibbonProfile.Points[index].Time=v,i==0?0:m.RibbonProfile.Points[i-1].Time+.001,i==m.RibbonProfile.Points.Count-1?1:m.RibbonProfile.Points[i+1].Time-.001,.01f);Number(P("Width"),m.RibbonProfile.Points[i].Value,v=>m.RibbonProfile.Points[index].Value=v,0,100,.01f);ImGui.PopID();}
            DrawParticleGradient(m);
        }
        if(particleModulePage=="material")DrawParticleGradient(m);
        if(particleModulePage=="drivers")DrawParticleDrivers(emitter);
        if(Button(P("restore-module")))Session.Edit(P("restore-module"),_=>
        {
            var defaults=new CreationParticleModules();
            foreach(var node in ParticleTargets(obj.ParticleEffect,emitter))if(node.Modules is {} target)
            {
                foreach(string field in fields){var property=typeof(CreationParticleModules).GetProperty(field)!;property.SetValue(target,property.GetValue(defaults));}
                if(particleModulePage=="drivers")target.Drivers.Clear();
                if(particleModulePage=="material")target.Gradient=new();
                if(particleModulePage=="shape"){target.Shape=ParticleSpawnShape.Point;target.Direction=ParticleDirection.Velocity;target.BindObject=target.BindBone=Guid.Empty;target.Mesh.Clear();target.Path=new CreationParticleModules().Path;}
                if(particleModulePage=="schedule")target.Bursts.Clear();
                if(particleModulePage=="geometry"){target.Geometry=ParticleGeometry.Particle;target.EndObject=target.EndBone=Guid.Empty;target.RibbonProfile=new();}
            }
        });
    }
    private void ParticleAttachment(string label,Guid selected,Guid bone,Action<Guid,Guid> set)
    {
        var obj=Project!.Objects.FirstOrDefault(o=>o.Id==selected);
        if(Combo(label,obj?.Name??P("none")))
        {
            if(ImGui.Selectable(P("none")))Session.Edit(label,_=>set(Guid.Empty,Guid.Empty));
            foreach(var candidate in Project.Objects)if(ImGui.Selectable(candidate.Name+"##"+candidate.Id,candidate==obj))Session.Edit(label,_=>set(candidate.Id,Guid.Empty));
            ImGui.EndCombo();
        }
        if(obj is not null&&Project.Models.Find(m=>m.Id==obj.ModelId) is {} model&&Combo(label+" / "+P("bone"),model.Parts.FirstOrDefault(p=>p.Id==bone)?.Name??P("none")))
        {
            if(ImGui.Selectable(P("none")))Session.Edit(label,_=>set(selected,Guid.Empty));
            foreach(var part in model.Parts)if(ImGui.Selectable(part.Name+"##"+part.Id,part.Id==bone))Session.Edit(label,_=>set(selected,part.Id));
            ImGui.EndCombo();
        }
    }
    private void DrawParticlePath(CreationParticleModules m)
    {
        for(int i=0;i<m.Path.Count;i++){int index=i;ImGui.PushID(i);VectorField(P("point")+" "+i,m.Path[i],v=>m.Path[index]=v);if(m.Path.Count>2&&Button(L("creation-delete")))Session.Edit(P("point"),_=>m.Path.RemoveAt(index));ImGui.PopID();}
        if(Button(P("add-point")))Session.Edit(P("add-point"),_=>m.Path.Add(m.Path.LastOrDefault()+new CreationVector(0,1,0)));
    }
    private void DrawParticleGradient(CreationParticleModules m)
    {
        Toggle(P("gradient"),m.Gradient.Enabled,v=>m.Gradient.Enabled=v);
        if(!m.Gradient.Enabled)return;
        var min=ImGui.GetCursorScreenPos();float width=Math.Max(1,ImGui.GetContentRegionAvail().X);var draw=ImGui.GetWindowDrawList();
        for(int i=0;i<64;i++){var color=m.Gradient.Sample(i/63d);draw.AddRectFilled(min+new Vector2(width*i/64,0),min+new Vector2(width*(i+1)/64,22),ImGui.ColorConvertFloat4ToU32(new(color.Color.Float(),(float)color.Alpha)));}ImGui.Dummy(new(width,26));
        for(int i=0;i<m.Gradient.Stops.Count;i++)
        {
            var stop=m.Gradient.Stops[i];int index=i;ImGui.PushID(i);
            Number(P("age"),stop.Time,v=>stop.Time=v,i==0?0:m.Gradient.Stops[i-1].Time+.001,i==m.Gradient.Stops.Count-1?1:m.Gradient.Stops[i+1].Time-.001,.01f);
            var color=new Vector4(stop.Color.Float(),(float)stop.Alpha);bool changed=ImGui.ColorEdit4(P("color"),ref color);
            Widget(P("gradient"),changed,()=>{stop.Color=new(color.X,color.Y,color.Z);stop.Alpha=color.W;});
            if(m.Gradient.Stops.Count>2&&Button(L("creation-delete")))Session.Edit(P("gradient"),_=>m.Gradient.Stops.RemoveAt(index));ImGui.PopID();
        }
        if(Button(P("add-color")))Session.Edit(P("add-color"),_=>
        {int gap=Enumerable.Range(0,m.Gradient.Stops.Count-1).MaxBy(i=>m.Gradient.Stops[i+1].Time-m.Gradient.Stops[i].Time);double time=(m.Gradient.Stops[gap].Time+m.Gradient.Stops[gap+1].Time)/2;var c=m.Gradient.Sample(time);m.Gradient.Stops.Insert(gap+1,new(){Time=time,Color=c.Color,Alpha=c.Alpha});});
    }
    private void DrawParticleDrivers(CreationParticleEmitter emitter)
    {
        var m=emitter.Modules!;
        if(Combo(P("parameter"),particleDriver)){foreach(var parameter in ParticleParameters.All)if(ImGui.Selectable(parameter.Id+" ("+parameter.Unit+")",parameter.Id==particleDriver))particleDriver=parameter.Id;ImGui.EndCombo();}
        if(!m.Drivers.TryGetValue(particleDriver,out var driver))
        {if(Button(P("add-driver")))Session.Edit(P("add-driver"),_=>m.Drivers[particleDriver]=new(){Clock=ParticleParameters.All.First(p=>p.Id==particleDriver).DefaultClock});return;}
        Choice(P("value-mode"),driver.Mode,v=>driver.Mode=v,Enum.GetValues<ParticleValueMode>().Select(v=>P(v.ToString())).ToArray());
        Choice(P("clock"),driver.Clock,v=>driver.Clock=v,Enum.GetValues<ParticleClock>().Select(v=>P("clock-"+v)).ToArray());
        if(driver.Mode==ParticleValueMode.Constant)Number(P("value"),driver.Constant,v=>driver.Constant=v);
        if(driver.Mode==ParticleValueMode.Curve&&Button(P("edit-curve")))particleCurveOpen=true;
        if(driver.Mode==ParticleValueMode.Expression)
        {
            string owner=emitter.Id+particleDriver;
            if(particleExpressionOwner!=owner){particleExpressionOwner=owner;particleExpressionDraft=driver.Expression;}
            ImGui.InputTextMultiline("##expression",ref particleExpressionDraft,4096,new(-1,100));
            if(Button(L("creation-apply")))Run(()=>{ParticleExpression.Compile(particleExpressionDraft);Session.Edit(P("Expression"),_=>driver.Expression=particleExpressionDraft);});
            Hint(string.Join(", ",ParticleExpression.Variables));Hint(string.Join(", ",ParticleExpression.Functions));
        }
        if(Button(P("remove-driver")))Session.Edit(P("remove-driver"),_=>m.Drivers.Remove(particleDriver));
    }
    private void DrawParticleCurveWindow()
    {
        if(Runtime.Exporting||Runtime.PerformanceActive||auxiliary.Length>0||!particleCurveOpen||SelectedObject?.ParticleEffect?.AllNodes().FirstOrDefault(e=>e.Id==particleEmitter)?.Modules is not {} m||!m.Drivers.TryGetValue(particleDriver,out var driver))return;
        if(host.TimelineDockId!=0)ImGui.SetNextWindowDockID(host.TimelineDockId,ImGuiCond.FirstUseEver);
        var viewport=GameImageRect();
        ImGui.SetNextWindowPos(viewport.Position+new Vector2(8,Math.Max(0,viewport.Size.Y-300)),ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new(Math.Max(240,Math.Min(640,viewport.Size.X-16)),300),ImGuiCond.FirstUseEver);
        if(ImGui.Begin(P("edit-curve")+"###particle-curve",ref particleCurveOpen))
        {
            var curve=driver.Curve;Choice(P("interpolation"),curve.Mode,v=>curve.Mode=v,Enum.GetValues<ParticleCurveMode>().Select(v=>P(v.ToString())).ToArray());
            var min=ImGui.GetCursorScreenPos();var size=new Vector2(Math.Max(120,ImGui.GetContentRegionAvail().X),120*DirectorImGuiTheme.Scale);
            double maxTime=Math.Max(1,curve.Points[^1].Time),low=Math.Min(0,curve.Points.Min(p=>p.Value)),high=Math.Max(1,curve.Points.Max(p=>p.Value));
            Vector2 Screen(double t,double v)=>min+new Vector2((float)(t/maxTime*size.X),(float)((high-v)/(high-low)*size.Y));
            ImGui.InvisibleButton("curve-canvas",size);var draw=ImGui.GetWindowDrawList();draw.AddRect(min,min+size,0xff777777);
            for(int i=1;i<=128;i++)draw.AddLine(Screen((i-1)*maxTime/128,curve.Sample((i-1)*maxTime/128)),Screen(i*maxTime/128,curve.Sample(i*maxTime/128)),0xff77dd99,2);
            for(int i=0;i<curve.Points.Count;i++){var p=curve.Points[i];var point=Screen(p.Time,p.Value);draw.AddCircleFilled(point,5,i==particleCurveSelection?0xff00ffff:0xffffffff);if(ImGui.IsItemClicked()&&Vector2.Distance(ImGui.GetIO().MousePos,point)<12){particleCurveSelection=i;particleCurveDragging=true;Session.BeginGesture();}}
            particleCurveSelection=Math.Clamp(particleCurveSelection,0,curve.Points.Count-1);var selected=curve.Points[particleCurveSelection];
            if(particleCurveDragging&&ImGui.IsItemActive()&&ImGui.IsMouseDragging(ImGuiMouseButton.Left))
            {var mouse=ImGui.GetIO().MousePos-min;selected.Time=Math.Clamp(mouse.X/size.X*maxTime,particleCurveSelection==0?0:curve.Points[particleCurveSelection-1].Time+.001,particleCurveSelection==curve.Points.Count-1?maxTime:curve.Points[particleCurveSelection+1].Time-.001);selected.Value=high-mouse.Y/size.Y*(high-low);Session.Changed();}
            if(particleCurveDragging&&ImGui.IsKeyPressed(ImGuiKey.Escape)){Session.CancelGesture();particleCurveDragging=false;}
            if(particleCurveDragging&&ImGui.IsMouseReleased(ImGuiMouseButton.Left)){Session.EndGesture(P("edit-curve"));particleCurveDragging=false;}
            Number(P("value"),selected.Value,v=>selected.Value=v);Number(P("tangent-in"),selected.In,v=>selected.In=v);Number(P("tangent-out"),selected.Out,v=>selected.Out=v);
            if(Button(P("add-point")))Session.Edit(P("add-point"),_=>{double time=curve.Points[^1].Time+.25;curve.Points.Add(new(){Time=time,Value=selected.Value});});
            SameLineIfFits(L("creation-delete"));if(curve.Points.Count>1&&Button(L("creation-delete")))Session.Edit(P("edit-curve"),_=>curve.Points.Remove(selected));
        }
        ImGui.End();
    }
}
