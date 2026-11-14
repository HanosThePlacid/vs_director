using System.Numerics;
using ImGuiNET;
using static VSDirector.Gui.DirectorCreationText;

namespace VSDirector.Gui;

internal sealed partial class DirectorCreationWorkspace
{
    private static float PickerBodyHeight() => Math.Max(1, ImGui.GetContentRegionAvail().Y
        - ImGui.GetTextLineHeightWithSpacing() - ImGui.GetFrameHeightWithSpacing() - ImGui.GetStyle().ScrollbarSize - ImGui.GetStyle().ItemSpacing.Y);

    private static float PickerSidebarWidth() => Math.Min(240 * DirectorImGuiTheme.Scale, ImGui.GetContentRegionAvail().X * .28f);

    // A horizontally scrollable footer keeps every action on one row even at high UI scale.
    private static void BeginPickerActions()
        => ImGui.BeginChild("picker-actions", new(0, ImGui.GetFrameHeightWithSpacing() + ImGui.GetStyle().ScrollbarSize), false,
            ImGuiWindowFlags.HorizontalScrollbar | ImGuiWindowFlags.NoScrollWithMouse);
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
