using System.Reflection.Emit;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VSDirector.Rendering;

[HarmonyPatch(typeof(ClientMain), nameof(ClientMain.RenderToDefaultFramebuffer))]
internal static class DirectorWorkspaceInterfaceVisibilityPatch
{
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var overlayFlag = AccessTools.Field(typeof(ClientMain), nameof(ClientMain.ShouldRender2DOverlays));
        var shouldRender = AccessTools.Method(typeof(DirectorWorkspaceViewportRuntime), nameof(DirectorWorkspaceViewportRuntime.ShouldRenderInterface));
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.LoadsField(overlayFlag))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = shouldRender;
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("VS Director: the game's GUI visibility gate has changed.");
    }
}

[HarmonyPatch(typeof(GuiManager), nameof(GuiManager.OnRenderFrameGUI))]
internal static class DirectorWorkspaceGameHudVisibilityPatch
{
    [HarmonyTranspiler]
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
    {
        var original = AccessTools.Method(typeof(GuiDialog), nameof(GuiDialog.ShouldReceiveRenderEvents));
        var shouldRender = AccessTools.Method(typeof(DirectorWorkspaceViewportRuntime), nameof(DirectorWorkspaceViewportRuntime.ShouldRenderDialog));
        int replaced = 0;
        foreach (var instruction in instructions)
        {
            if (instruction.Calls(original))
            {
                instruction.opcode = OpCodes.Call;
                instruction.operand = shouldRender;
                replaced++;
            }
            yield return instruction;
        }
        if (replaced != 1) throw new InvalidOperationException("VS Director: the game's dialog render loop has changed.");
    }
}

[HarmonyPatch(typeof(SystemRenderPlayerAimAcc), "OnRenderFrame2DOverlay")]
internal static class DirectorWorkspaceAimAccuracyVisibilityPatch
{
    [HarmonyPrefix]
    private static bool Prefix() => !DirectorWorkspaceViewportRuntime.HideGameHud;
}

/// <summary>
/// Hides Ortho-stage renderers registered by other mods while the game HUD is hidden.
/// The workspace keeps the Ortho stage running for its own GUI, which used to leave
/// mod HUD overlays visible in hidden-HUD shots.
/// </summary>
[HarmonyPatch(typeof(ClientEventManager), nameof(ClientEventManager.TriggerRenderStage))]
internal static class DirectorWorkspaceModOverlayVisibilityPatch
{
    [HarmonyPrefix]
    private static bool Prefix(ClientEventManager __instance, EnumRenderStage stage, float dt)
    {
        if (stage != EnumRenderStage.Ortho || !DirectorWorkspaceViewportRuntime.HideGameHud)
        {
            return true;
        }
        List<RenderHandler> renderers = __instance.renderersByStage[(int)stage];
        for (int i = 0; i < renderers.Count; i++)
        {
            IRenderer renderer = renderers[i].Renderer;
            if (DirectorWorkspaceViewportRuntime.ShouldRenderOrthoRenderer(renderer))
            {
                renderer.OnRenderFrame(dt, stage);
            }
        }
        return false;
    }
}
