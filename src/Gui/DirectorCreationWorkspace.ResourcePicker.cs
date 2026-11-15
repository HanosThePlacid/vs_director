using System.Numerics;
using ImGuiNET;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private float pickerFooterHeight;
    private float PickerBodyHeight()
    {
        string[] actions=auxiliary=="actors"?["creation-actor-import-selected","creation-record-world-actor","creation-cancel"]:
            particleChooseLibrary?["creation-particle-import-selected","creation-particle-blank","creation-cancel"]:
            ["creation-particle-import-selected","creation-particle-blank","creation-particle-rescan","creation-cancel"];
        float width=actions.Sum(key=>ImGui.CalcTextSize(L(key)).X+ImGui.GetStyle().FramePadding.X*2)+(actions.Length-1)*ImGui.GetStyle().ItemSpacing.X;
        pickerFooterHeight=ImGui.GetFrameHeightWithSpacing()+(width>ImGui.GetContentRegionAvail().X?ImGui.GetStyle().ScrollbarSize:0);
        return Math.Max(1,ImGui.GetContentRegionAvail().Y-ImGui.GetTextLineHeightWithSpacing()-pickerFooterHeight-ImGui.GetStyle().ItemSpacing.Y);
    }
    private static float PickerSidebarWidth() => Math.Min(240 * DirectorImGuiTheme.Scale, ImGui.GetContentRegionAvail().X * .28f);
    private void BeginPickerActions()
        => ImGui.BeginChild("picker-actions", new(0,pickerFooterHeight), false,ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
    private static bool PickerAction(string key, bool first = false)
    {
        if (!first) ImGui.SameLine();
        return ImGui.Button(L(key));
    }
    private static void EndPickerActions() => ImGui.EndChild();

    private static bool BeginPickerGrid(string id)
    {
        int columns = Math.Clamp((int)(ImGui.GetContentRegionAvail().X / (180 * DirectorImGuiTheme.Scale)), 1, 32);
        ImGui.PushStyleVar(ImGuiStyleVar.CellPadding, new Vector2(12, 10) * DirectorImGuiTheme.Scale);
        return ImGui.BeginTable(id, columns, ImGuiTableFlags.SizingStretchSame | ImGuiTableFlags.PadOuterX);
    }
    private static void EndPickerGrid(bool open)
    {
        if (open) ImGui.EndTable();
        ImGui.PopStyleVar();
    }
    private static void PickerCardLabel(ImDrawListPtr draw, Vector2 min, Vector2 max, float previewBottom,
        string name, bool selected, bool hovered)
    {
        draw.PushClipRect(new(min.X + 4, Math.Max(min.Y, previewBottom)), max - new Vector2(2), true);
        string label = name;
        float width = Math.Max(1, max.X - min.X - 10);
        while (label.Length > 0 && ImGui.CalcTextSize(label + (label.Length < name.Length ? "…" : "")).X > width) label = label[..^1];
        label += label.Length < name.Length ? "…" : "";
        draw.AddText(new(min.X + Math.Max(4, (max.X - min.X - ImGui.CalcTextSize(label).X) / 2), previewBottom + 4),
            ImGui.GetColorU32(ImGuiCol.Text), label);
        draw.PopClipRect();
        // Empty previews share the window background; only the selection/hover outline is drawn.
        if (selected || hovered) draw.AddRect(min, max, ImGui.GetColorU32(selected ? ImGuiCol.CheckMark : ImGuiCol.ButtonHovered), 4, ImDrawFlags.None, selected ? 2 : 1);
    }

    private static Matrix4x4 PickerRotation(double time) => Matrix4x4.CreateRotationY((float)(time * .35)) * Matrix4x4.CreateRotationX(-.35f);
}
