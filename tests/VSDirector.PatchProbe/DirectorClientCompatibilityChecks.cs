using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;
using VSDirector;

internal static class DirectorClientCompatibilityChecks
{
    internal static void Run()
    {
        var assembly = typeof(VSDirectorModSystem).Assembly;
        var adapter = assembly.GetType("VSDirector.Compatibility.DirectorClientCompatibility", true)!;
        var camera = (PlayerCamera)RuntimeHelpers.GetUninitializedObject(typeof(PlayerCamera));
        var set = (Action<PlayerCamera, float>)AccessTools.Field(adapter, "SetFov").GetValue(null)!;
        var get = (System.Func<PlayerCamera, float>)AccessTools.Field(adapter, "GetFov").GetValue(null)!;
        foreach (float fov in new[] { 30f, 70f, 110f })
        {
            set(camera, fov);
            if (get(camera) != fov) throw new Exception("Camera FOV did not round trip.");
        }

        var runtime = assembly.GetType("VSDirector.Rendering.DirectorWorkspaceViewportRuntime", true)!;
        var patch = assembly.GetType("VSDirector.Rendering.DirectorWorkspacePickingRayPatch", true)!;
        var state = AccessTools.Field(runtime, "state");
        object? saved = state.GetValue(null);
        var input = ProbeDispatchProxy.Create<IInputAPI>((m, _) => ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var gui = ProbeDispatchProxy.Create<IGuiAPI>((m, _) => m.Name == "get_OpenedGuis" ? new List<GuiDialog>() : ProbeDispatchProxy.DefaultValue(m.ReturnType));
        var render = ProbeDispatchProxy.Create<IRenderAPI>((m, _) => m.Name switch
        { "get_FrameWidth" => 1600, "get_FrameHeight" => 900, _ => ProbeDispatchProxy.DefaultValue(m.ReturnType) });
        var api = ProbeDispatchProxy.Create<ICoreClientAPI>((m, _) => m.Name switch
        { "get_Input" => input, "get_Gui" => gui, "get_Render" => render, _ => ProbeDispatchProxy.DefaultValue(m.ReturnType) });
        var game = (ClientMain)RuntimeHelpers.GetUninitializedObject(typeof(ClientMain));
        try
        {
            // Half-size viewport at (200,100): its center must pick the center
            // of the original frame for either native method signature.
            AccessTools.Method(runtime, "Configure").Invoke(null, [new object(), api, 200, 100, 800, 450, false]);
            foreach (bool explicitCoordinates in new[] { false, true })
            {
                game.MouseCurrentX = explicitCoordinates ? 17 : 600;
                game.MouseCurrentY = explicitCoordinates ? 29 : 325;
                object[] nativeArgs = explicitCoordinates ? [game, 600, 325] : [game];
                object?[] args = [game, nativeArgs, null];
                AccessTools.Method(patch, "Prefix").Invoke(null, args);
                if (explicitCoordinates
                    ? (int)nativeArgs[1] != 800 || (int)nativeArgs[2] != 450 || game.MouseCurrentX != 17 || game.MouseCurrentY != 29
                    : game.MouseCurrentX != 800 || game.MouseCurrentY != 450)
                    throw new Exception("Docked viewport picking did not map the native pointer source.");
                AccessTools.Method(patch, "Postfix").Invoke(null, [game, args[2]]);
                if (game.MouseCurrentX != (explicitCoordinates ? 17 : 600) || game.MouseCurrentY != (explicitCoordinates ? 29 : 325))
                    throw new Exception("Picking left the game's mouse coordinates modified.");
            }
            state.SetValue(null, null);
            object[] unchanged = [game, 600, 325];
            AccessTools.Method(patch, "Prefix").Invoke(null, [game, unchanged, null]);
            if ((int)unchanged[1] != 600 || (int)unchanged[2] != 325)
                throw new Exception("Picking changed while the workspace was closed.");
        }
        finally { state.SetValue(null, saved); }
        Console.WriteLine("PASS version-adapted camera FOV and legacy/explicit viewport picking with mouse restoration");
    }
}
