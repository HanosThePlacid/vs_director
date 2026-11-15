using System.Collections;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using System.Text.Json.Nodes;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Config;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckParticlePicker(object workspace, object runtime, DirectorCreationSession session, Action<string> capture)
    {
        var type = workspace.GetType();
        var original = session.Project!;
        var io = ImGui.GetIO(); var display = io.DisplaySize; float guiScale = RuntimeEnv.GUIScale;
        var resourceManager = AccessTools.Property(runtime.GetType(), "ParticleResources").GetValue(runtime)!;
        AccessTools.Field(resourceManager.GetType(), "scanned").SetValue(resourceManager, true);
        var entries = (IList)AccessTools.Property(resourceManager.GetType(), "Entries").GetValue(resourceManager)!;
        var resourceType = type.Assembly.GetType("VSDirector.Runtime.DirectorParticleResource", true)!;
        var cache = (IDictionary)AccessTools.Field(type, "particlePreviews").GetValue(workspace)!;
        void Set(string field, object value) => AccessTools.Field(type, field).SetValue(workspace, value);
        object? Call(string method, params object[] args) => AccessTools.Method(type, method).Invoke(workspace, args);
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        var properties = CreationParticleFields.Defaults();
        properties["Quantity"] = CreationParticleFields.Nat(4);
        properties["GravityEffect"] = CreationParticleFields.Nat(0);
        properties["TerrainCollision"] = false;
        properties["HsvaColor"] = new JsonArray(CreationParticleFields.Nat(0), CreationParticleFields.Nat(255), CreationParticleFields.Nat(255), CreationParticleFields.Nat(255));
        properties["Velocity"] = new JsonArray(CreationParticleFields.Nat(.2), CreationParticleFields.Nat(.8), CreationParticleFields.Nat(0));
        properties["ParticleModel"] = "Cube";
        object Resource(string id, string variant, string kind, int index) => Activator.CreateInstance(resourceType,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [id, "block:game:probe-*", variant, "game:probe", kind, index, properties.DeepClone(), null], null)!;
        var red1 = Resource("probe/red/0", "game:probe-red", "block", 0);
        var red2 = Resource("probe/red/1", "game:probe-red", "block", 1);
        entries.Add(red1); entries.Add(red2);
        entries.Add(Resource("probe/blue/0", "game:probe-blue", "block", 0));
        entries.Add(Resource("probe/item/0", "game:probe-red", "item", 0));
        Array native = Array.CreateInstance(resourceType, 2); native.SetValue(red1, 0); native.SetValue(red2, 1);
        Vector2 cardMin = default, cardMax = default;
        bool redVertices = false;
        void CardFrame()
        {
            ImGui.NewFrame(); ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(210, 280));
            ImGui.Begin("particle-card-interaction", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoResize);
            Call("DrawParticleCard", native, Stopwatch.StartNew());
            cardMin = ImGui.GetItemRectMin(); cardMax = ImGui.GetItemRectMax();
            foreach(var card in cache.Values)
            {
                int texture=(int)AccessTools.Property(card!.GetType(),"Texture").GetValue(card)!;
                if(texture==0)continue;
                int previous=OpenTK.Graphics.OpenGL4.GL.GetInteger(OpenTK.Graphics.OpenGL4.GetPName.TextureBinding2D);
                OpenTK.Graphics.OpenGL4.GL.BindTexture(OpenTK.Graphics.OpenGL4.TextureTarget.Texture2D,texture);
                var pixels=new byte[256*256*4];OpenTK.Graphics.OpenGL4.GL.GetTexImage(OpenTK.Graphics.OpenGL4.TextureTarget.Texture2D,0,OpenTK.Graphics.OpenGL4.PixelFormat.Rgba,OpenTK.Graphics.OpenGL4.PixelType.UnsignedByte,pixels);
                OpenTK.Graphics.OpenGL4.GL.BindTexture(OpenTK.Graphics.OpenGL4.TextureTarget.Texture2D,previous);
                redVertices|=Enumerable.Range(0,256*256).Any(i=>pixels[i*4]>128&&pixels[i*4+1]==0&&pixels[i*4+2]==0&&pixels[i*4+3]>0);
            }
            ImGui.End(); ImGui.Render();
        }
        void LibraryFrame(float width, float height, bool inspect = false)
        {
            ImGui.NewFrame(); ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(width, height));
            ImGui.Begin("particle-picker-layout", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoResize);
            ImGui.SetWindowFontScale(RuntimeEnv.GUIScale);
            Call("DrawParticleResources");
            if (inspect) Check(ImGui.GetScrollMaxX() <= 1, $"Particle picker overflowed horizontally at {Lang.CurrentLocale}/{RuntimeEnv.GUIScale}/{width}: {ImGui.GetScrollMaxX()}");
            ImGui.SetWindowFontScale(1); ImGui.End(); ImGui.Render();
        }
        try
        {
            session.Open(new() { WorldId = "probe-world", Duration = 10 }); session.Seek(1.25);
            io.DisplaySize = new(1600, 1200); RuntimeEnv.GUIScale = 1;
            Call("OpenParticlePicker");
            Check((string)AccessTools.Field(type, "auxiliary").GetValue(workspace)! == "particles" && session.Project!.Objects.Count == 0,
                "Opening particle picker created an object");
            Set("particleSearch", "probe-red");
            for (int frame = 0; frame < 12; frame++) LibraryFrame(760, 640);
            Check(cache.Count == 2, "Search did not group effects by source and variant");
            var preview = cache["probe/red/0"]!;
            var simulation = (DirectorCreationParticleSimulation)AccessTools.Property(preview.GetType(), "Simulation").GetValue(preview)!;
            Check(simulation.Ready && simulation.Particles.Count > 0, "Particle card has no simulated preview");
            Check(session.Time == 1.25 && session.Project!.Objects.Count == 0 && !session.Dirty, "Preview changed the scene or time");
            double previewTime = simulation.Time;
            Set("particleSearch", "does-not-exist");
            LibraryFrame(760, 640); LibraryFrame(760, 640);
            Check(simulation.Time == previewTime, "Hidden search results continued simulating");

            CardFrame(); CardFrame();
            Check(Math.Abs((cardMax.X - cardMin.X) - (cardMax.Y - cardMin.Y)) < .1, "Particle card is not square");
            Check(redVertices, "Particle card did not draw its native red cube particles");
            io.MousePos = (cardMin + cardMax) * .5f; CardFrame();
            io.MouseDown[0] = true; CardFrame(); io.MouseDown[0] = false; CardFrame();
            Check((string)AccessTools.Field(type, "particleResourceId").GetValue(workspace)! == "probe/red/0" && session.Project!.Objects.Count == 0,
                "Card click failed to select without importing");
            Call("ImportParticleResource", new CreationVector(2, 3, 4));
            var imported = session.Project!.Objects.Single();
            Check(imported.ParticleStart == 1.25 && imported.Transform.Position == new CreationVector(2, 3, 4), "Import lost trigger time or placement");
            Check(imported.ParticleEffect!.Emitters.Count == 2 && imported.ParticleEffect.Emitters.Select(e => e.Source).SequenceEqual(new[] { "probe/red/0", "probe/red/1" }),
                "Import failed to copy all emitters or mixed different source kinds");
            imported.ParticleEffect.Emitters[0].Properties["Quantity"] = CreationParticleFields.Nat(99);
            Check(CreationParticleFields.Number((JsonObject)CreationParticleFields.Find((JsonObject)AccessTools.Property(resourceType, "Properties").GetValue(red1)!, "Quantity")!, "avg") == 4,
                "Imported effect mutated native source properties");
            session.Undo(); Check(session.Project!.Objects.Count == 0, "Particle import undo failed");
            session.Redo(); Check(session.Project!.Objects.Count == 1, "Particle import redo failed");

            Set("particleSearch", "probe-red");
            foreach (string language in new[] { "zh-cn", "en" })
            foreach (float scale in new[] { 1f, 1.5f, 2f })
            foreach (float width in new[] { 220f, 320f, 760f })
            {
                RuntimeEnv.GUIScale = scale; LoadCreationProbeLanguage(language);
                for (int frame = 0; frame < 3; frame++) LibraryFrame(width * scale, 640, frame == 2);
            }
            Set("particleChooseLibrary",true);Set("particlePresetSearch","");
            long libraryRevision=session.Revision;double libraryTime=session.Time;
            foreach(string language in new[]{"zh-cn","en"})
            foreach(float scale in new[]{1f,2f})
            foreach(float width in new[]{220f,760f})
            {
                RuntimeEnv.GUIScale=scale;LoadCreationProbeLanguage(language);
                for(int frame=0;frame<3;frame++)LibraryFrame(width*scale,640,frame==2);
            }
            Check(session.Revision==libraryRevision&&session.Time==libraryTime,"Preset library browsing changed document or timeline");
            Set("particleChooseLibrary",false);Call("ClearParticlePreviews");
            Console.WriteLine("PASS fullscreen preset library/examples, isolated simulation and bilingual narrow layouts");
            Console.WriteLine("PASS particle picker search/tree/grid, live native cube thumbnails, explicit selection/import, source isolation, timing, undo/redo and narrow Chinese/English layouts at 100/150/200%");
            io.DisplaySize = new(960,640); RuntimeEnv.GUIScale = 1; LoadCreationProbeLanguage("zh-cn");
            Set("auxiliary", "particles"); Set("particleSearch", "");
            for(int frame=0;frame<6;frame++) { ImGui.NewFrame(); Call("DrawAuxiliary"); ImGui.Render(); }
            capture("fullscreen-particles");
        }
        finally
        {
            io.MouseDown[0] = false; io.MousePos = new(-1000); io.DisplaySize = display; RuntimeEnv.GUIScale = guiScale;
            Set("particleChooseLibrary",false); Set("particleSearch", ""); Set("particleResourceId", ""); Set("auxiliary", ""); Call("ClearParticlePreviews"); entries.Clear();
            session.Open(original); LoadCreationProbeLanguage("zh-cn");
        }
    }
}
