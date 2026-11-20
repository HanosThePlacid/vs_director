using System.Numerics;
using System.Reflection;
using HarmonyLib;
using ImGuiNET;
using OpenTK.Windowing.Desktop;

internal static partial class DirectorImGuiProbe
{
    private static unsafe void CheckDefaultFontLifecycle(Assembly assembly)
    {
        var runtime = assembly.GetType("VSDirector.Gui.DirectorImGuiRuntime", true)!;
        var useFont = AccessTools.Method(runtime, "UseFont");
        nint savedContext = ImGui.GetCurrentContext();
        nint context = ImGui.CreateContext();
        try
        {
            ImGui.SetCurrentContext(context);
            var io = ImGui.GetIO();
            io.DisplaySize = new Vector2(960, 640);
            io.DeltaTime = 1f / 60;
            io.NativePtr->IniFilename = null;
            var first = io.Fonts.AddFontDefault();
            var selected = io.Fonts.AddFontDefault();
            io.NativePtr->FontDefault = selected.NativePtr;
            io.Fonts.Build();
            float textAdvance = selected.IndexAdvanceX['A'];
            using var window = new NativeWindow(new NativeWindowSettings
            {
                ClientSize = new OpenTK.Mathematics.Vector2i(960, 640),
                StartVisible = false,
                API = OpenTK.Windowing.Common.ContextAPI.OpenGL,
                APIVersion = new Version(3, 3)
            });
            window.Context.MakeCurrent();
            var rendererType = typeof(ImGuiController_OpenTK.ImGuiController).Assembly.GetType("ImGuiController_OpenTK.ImGuiRenderer", true)!;
            using var renderer = (IDisposable)Activator.CreateInstance(rendererType, ImGui.GetMainViewport(), window)!;
            var upload = AccessTools.Method(rendererType, "RecreateFontDeviceTexture");
            var icons = (Dictionary<string, string>)AccessTools.Field(assembly.GetType("VSDirector.Gui.DirectorImGuiIcons"), "Glyphs").GetValue(null)!;
            CheckIcons(selected);
            if (io.Fonts.Fonts.Size != 2 || selected.IndexAdvanceX['A'] != textAdvance
                || first.FindGlyphNoFallback(icons["play"][0]).NativePtr != null)
                throw new Exception("Icon merge replaced game text or targeted the wrong default font.");
            Frame(selected, first);

            // Match FontSettings: clear the same atlas, replace the default,
            // build it, then ask the renderer to upload its new texture.
            foreach (float size in new[] { 18f, 24f, 18f })
            {
                io.Fonts.Clear();
                io.NativePtr->FontDefault = null;
                var config = new ImFontConfigPtr(ImGuiNative.ImFontConfig_ImFontConfig());
                try
                {
                    config.SizePixels = size;
                    selected = io.Fonts.AddFontDefault(config);
                }
                finally { config.Destroy(); }
                io.NativePtr->FontDefault = selected.NativePtr;
                io.Fonts.Build();
                // Before upload, UseFont must already use the replacement.
                Frame(selected, selected);
                upload.Invoke(renderer, null);
                CheckIcons(selected);
                Frame(selected, selected);
                int configCount = io.Fonts.ConfigData.Size;
                upload.Invoke(renderer, null);
                if (io.Fonts.ConfigData.Size != configCount || io.Fonts.Fonts.Size != 1 || selected.FontSize != size)
                    throw new Exception("Repeated texture uploads duplicated fonts or changed the game font size.");
            }

            nint other = ImGui.CreateContext();
            try
            {
                ImGui.SetCurrentContext(other);
                var otherIO = ImGui.GetIO();
                otherIO.DisplaySize = io.DisplaySize;
                otherIO.DeltaTime = io.DeltaTime;
                otherIO.NativePtr->IniFilename = null;
                var otherFont = otherIO.Fonts.AddFontDefault();
                otherIO.Fonts.Build();
                Frame(otherFont, otherFont);
            }
            finally { ImGui.DestroyContext(other); ImGui.SetCurrentContext(context); }
            Frame(selected, selected);
            Console.WriteLine("PASS game default font: text metrics preserved, icons restored after 3 shared-atlas rebuilds, idempotent GPU uploads, context switches and balanced font scopes");

            void CheckIcons(ImFontPtr liveFont)
            {
                foreach (string glyph in icons.Values)
                    if (liveFont.FindGlyphNoFallback(glyph[0]).NativePtr == null)
                        throw new Exception("Editor icon missing from the live game default font.");
            }

            void Frame(ImFontPtr expected, ImFontPtr outer)
            {
                ImGui.NewFrame();
                ImGui.PushFont(outer);
                using ((IDisposable)useFont.Invoke(null, [false])!)
                {
                    if (ImGui.GetFont().NativePtr != expected.NativePtr)
                        throw new Exception("Director retained a previous atlas/context font.");
                    ImGui.Begin("font-lifecycle");
                    ImGui.TextUnformatted("Game default text");
                    ImGui.End();
                }
                if (ImGui.GetFont().NativePtr != outer.NativePtr) throw new Exception("Director did not restore the outer font.");
                ImGui.PopFont();
                ImGui.Render();
            }
        }
        finally { ImGui.DestroyContext(context); ImGui.SetCurrentContext(savedContext); }
    }
}
