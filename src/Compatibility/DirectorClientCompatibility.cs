using System.Linq.Expressions;
using Vintagestory.API.Client;
using Vintagestory.Client.NoObf;

namespace VSDirector.Compatibility;

internal static class DirectorClientCompatibility
{
    internal static readonly Func<PlayerCamera, float> GetFov = DirectorGameCompatibility.Getter<PlayerCamera, float>("Fov");
    internal static readonly Action<PlayerCamera, float> SetFov = DirectorGameCompatibility.Setter<PlayerCamera, float>("Fov");
    private static readonly Action<GuiElementEditableTextBase, string, bool> SetText = CreateSetText();

    internal static void SetDirectorValue(this GuiElementEditableTextBase input, string value, bool setCaretPosToEnd = true)
        => SetText(input, value, setCaretPosToEnd);

    private static Action<GuiElementEditableTextBase, string, bool> CreateSetText()
    {
        var input = Expression.Parameter(typeof(GuiElementEditableTextBase));
        var value = Expression.Parameter(typeof(string));
        var caret = Expression.Parameter(typeof(bool));
        var call = Expression.Call(input, typeof(GuiElementEditableTextBase).GetMethod("SetValue", [typeof(string), typeof(bool)])!, value, caret);
        // 1.22 returns void; 1.23 returns whether its validator accepted the
        // edit. These callers never consumed a result, so retain that behavior.
        return Expression.Lambda<Action<GuiElementEditableTextBase, string, bool>>(
            Expression.Block(call, Expression.Empty()), input, value, caret).Compile();
    }
}
