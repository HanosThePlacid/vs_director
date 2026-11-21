using System.Collections;
using System.Diagnostics;
using System.Numerics;
using System.Reflection;
using HarmonyLib;
using ImGuiNET;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using VSDirector.Data;

internal static partial class DirectorImGuiProbe
{
    private static void CheckFullscreenResourcePickers(object workspace, object runtime, DirectorCreationSession session, Action<string> capture)
    {
        var type = workspace.GetType(); var io = ImGui.GetIO();
        CheckPickerPreviewRegressions(type);
        var originalApi = (ICoreClientAPI)AccessTools.Field(runtime.GetType(), "api").GetValue(runtime)!;
        var display = io.DisplaySize; float scale = RuntimeEnv.GUIScale, delta = io.DeltaTime;
        string locale = Lang.CurrentLocale;
        var cache = (IDictionary)AccessTools.Field(type, "actorPickerPreviews").GetValue(workspace)!;
        var previewType = type.GetNestedType("ActorPickerPreview", BindingFlags.NonPublic)!;
        var entryType = type.GetNestedType("ActorPickerEntry", BindingFlags.NonPublic)!;
        object? Call(string method, params object[] args) => AccessTools.Method(type, method).Invoke(workspace, args);
        void Set(string field, object value) => AccessTools.Field(type, field).SetValue(workspace, value);
        void Check(bool ok, string message) { if (!ok) throw new Exception(message); }
        var entities = new List<EntityProperties> { new() { Code = new("game:picker-probe"), Client = new([], new()) { Shape = new() { Base = new("game:picker-probe") } } } };
        var world = ProbeDispatchProxy.Create<IClientWorldAccessor>((method, _) => method.Name switch
        {
            "get_Player" => originalApi.World.Player, "get_SavegameIdentifier" => "probe-world",
            "get_LoadedEntities" => new Dictionary<long, Entity>(), "get_EntityTypes" => entities,
            _ => ProbeDispatchProxy.DefaultValue(method.ReturnType)
        });
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((method, _) => method.Name == "get_World" ? world : ProbeDispatchProxy.DefaultValue(method.ReturnType));
        var mesh = new MeshData(4, 6);
        mesh.AddVertex(-1, 0, 0, 0, 0, -1); mesh.AddVertex(1, 0, 0, 1, 0, -1); mesh.AddVertex(1, 2, 0, 1, 1, -1); mesh.AddVertex(-1, 2, 0, 0, 1, -1);
        foreach(int index in new[]{0,1,2,0,2,3})mesh.AddIndex(index);
        mesh.TextureIds = [(int)io.Fonts.TexID]; mesh.TextureIndices = [0]; mesh.TextureIndicesCount = 1;
        var preview = Activator.CreateInstance(previewType, true)!;
        AccessTools.Method(previewType, "SetMesh").Invoke(preview, [mesh]);
        int imported = 0;
        var entry = Activator.CreateInstance(entryType, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
            ["native/game:picker-probe", "Probe actor", "game:picker-probe", 0L, "native/game", (Action)(() => imported++), null], null)!;
        Vector2 min = default, max = default;
        Vector2[] CardFrame()
        {
            ImGui.NewFrame(); ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(240, 300));
            ImGui.Begin("actor-card-probe", ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoResize);
            int first = ImGui.GetWindowDrawList().VtxBuffer.Size;
            Call("DrawActorPickerCard", entry, Stopwatch.StartNew());
            min = ImGui.GetItemRectMin(); max = ImGui.GetItemRectMax();
            var vertices = ImGui.GetWindowDrawList().VtxBuffer;
            var points = Enumerable.Range(first, Math.Min(4, vertices.Size - first)).Select(i => vertices[i].pos).ToArray();
            ImGui.End(); ImGui.Render(); return points;
        }
        try
        {
            AccessTools.Field(runtime.GetType(), "api").SetValue(runtime, api);
            io.DisplaySize = new(1440, 900); RuntimeEnv.GUIScale = 1; io.DeltaTime = 1f / 30;
            Set("actorPickerSelected", ""); cache["native/game:picker-probe"] = preview;
            CardFrame();
            var target=AccessTools.Field(previewType,"target").GetValue(preview)!;
            byte[] ReadPreview()
            {
                int old=OpenTK.Graphics.OpenGL4.GL.GetInteger(OpenTK.Graphics.OpenGL4.GetPName.TextureBinding2D);
                int texture=(int)AccessTools.Property(target.GetType(),"Texture").GetValue(target)!;
                OpenTK.Graphics.OpenGL4.GL.BindTexture(OpenTK.Graphics.OpenGL4.TextureTarget.Texture2D,texture);
                var pixels=new byte[256*256*4];OpenTK.Graphics.OpenGL4.GL.GetTexImage(OpenTK.Graphics.OpenGL4.TextureTarget.Texture2D,0,OpenTK.Graphics.OpenGL4.PixelFormat.Rgba,OpenTK.Graphics.OpenGL4.PixelType.UnsignedByte,pixels);
                OpenTK.Graphics.OpenGL4.GL.BindTexture(OpenTK.Graphics.OpenGL4.TextureTarget.Texture2D,old);return pixels;
            }
            var before = ReadPreview();
            for (int i = 0; i < 60; i++) CardFrame();
            Check(!before.SequenceEqual(ReadPreview()), "Actor preview did not rotate its textured geometry");
            io.MousePos = (min + max) / 2; CardFrame(); io.MouseDown[0] = true; CardFrame(); io.MouseDown[0] = false; CardFrame();
            Check((string)AccessTools.Field(type, "actorPickerSelected").GetValue(workspace)! == "native/game:picker-probe" && imported == 0,
                "Actor card must select without importing");
            io.MousePos = new(-1000);
            double time = session.Time; long revision = session.Revision;
            foreach (string language in new[] { "zh-cn", "en" })
            foreach (float uiScale in new[] { 1f, 1.5f, 2f })
            foreach (Vector2 viewport in new[] { new Vector2(640, 480), new Vector2(1440, 900) })
            foreach (string picker in new[] { "particles", "actors" })
            {
                LoadCreationProbeLanguage(language); RuntimeEnv.GUIScale = uiScale; io.DisplaySize = viewport;
                Set("auxiliary", picker); Set("search", ""); cache["native/game:picker-probe"] = preview;
                for (int frame = 0; frame < 3; frame++)
                {
                    ImGui.NewFrame(); Call("DrawAuxiliary");
                    ImGui.Begin("###creation-picker-" + picker);
                    Check(Vector2.Distance(ImGui.GetWindowPos(), Vector2.Zero) < 1 && Vector2.Distance(ImGui.GetWindowSize(), viewport) < 1,
                        "Picker failed to fill viewport: " + picker);
                    if (frame == 2) Check(ImGui.GetScrollMaxX() <= 1 && ImGui.GetScrollMaxY() <= 1,
                        $"Fullscreen picker outer window scrolls: {picker}/{language}/{uiScale}/{viewport}: {ImGui.GetScrollMaxX()},{ImGui.GetScrollMaxY()}");
                    ImGui.End(); ImGui.Render();
                }
            }
            Check(session.Time == time && session.Revision == revision, "Browsing actors or particles changed document/time");
            io.DisplaySize = new(960,640); RuntimeEnv.GUIScale = 1; LoadCreationProbeLanguage("zh-cn");
            Set("auxiliary", "actors"); cache["native/game:picker-probe"] = preview;
            for(int frame=0;frame<3;frame++) { ImGui.NewFrame(); Call("DrawAuxiliary"); ImGui.Render(); }
            capture("fullscreen-actors");
            foreach(string language in new[]{"zh-cn","en"})
            foreach(float uiScale in new[]{1f,2f})
            {
                LoadCreationProbeLanguage(language); RuntimeEnv.GUIScale=uiScale;
                ImGui.NewFrame(); ImGui.SetNextWindowPos(Vector2.Zero); ImGui.SetNextWindowSize(new(320,300));
                ImGui.Begin("picker-footer-probe",ImGuiWindowFlags.NoSavedSettings | ImGuiWindowFlags.NoResize);
                ImGui.SetWindowFontScale(uiScale); Call("BeginPickerActions");
                float? row=null;
                foreach(string key in new[]{"creation-particle-import-selected","creation-particle-blank","creation-particle-rescan","creation-cancel"})
                {
                    Call("PickerAction",key,row is null);
                    float y=ImGui.GetItemRectMin().Y; if(row is {} first)Check(Math.Abs(y-first)<1,"Picker footer wrapped its action buttons");
                    row=y;
                }
                Call("EndPickerActions"); ImGui.SetWindowFontScale(1); ImGui.End(); ImGui.Render();
            }
            Console.WriteLine("PASS fullscreen actor/particle pickers fit 640/1440 viewports at Chinese/English 100/150/200%; actor textured geometry rotates and selection never imports; browsing preserves document/time");
        }
        finally
        {
            AccessTools.Field(runtime.GetType(), "api").SetValue(runtime, originalApi);
            io.DisplaySize = display; io.DeltaTime = delta; io.MouseDown[0] = false; io.MousePos = new(-1000); RuntimeEnv.GUIScale = scale;
            Set("auxiliary", ""); Set("actorPickerSelected", ""); Call("ClearActorPickerPreviews"); ((IDisposable)preview).Dispose(); LoadCreationProbeLanguage(locale);
        }
    }
}
