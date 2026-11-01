using Terminal.Gui.Drawing;
using Terminal.Gui.ViewBase;
using Attribute = Terminal.Gui.Drawing.Attribute;

namespace Matrix.RustSdk.Examples.TuiClient.Rendering;

/// <summary>
/// A horizontal line with a text in it (<c>── alice is typing… ──────</c>), separates the timeline from the composer.
/// </summary>
public sealed class RuleView : View
{
    public RuleView()
    {
        Height = 1;
        CanFocus = false;
    }

    public string Label
    {
        get;
        set
        {
            if (field != value)
            {
                field = value;
                SetNeedsDraw();
            }
        }
    } = "";

    protected override bool OnDrawingContent(DrawContext? context)
    {
        Attribute normal = GetAttributeForRole(VisualRole.Normal);
        int width = Viewport.Width;
        Move(0, 0);
        SetAttribute(Theme.Resolve(new Span("", Role.Dim), normal, false));
        string prefix = Label.Length > 0 ? "── " : "";
        string label = TextLayout.Truncate(Label, Math.Max(0, width - prefix.Length - 1));
        AddStr(prefix);
        SetAttribute(Theme.Resolve(new Span("", Role.Quote), normal, false));
        AddStr(label);
        SetAttribute(Theme.Resolve(new Span("", Role.Dim), normal, false));
        int used = prefix.Length + TextLayout.Width(label);
        string rest =
            (label.Length > 0 ? " " : "") + new string('─', Math.Max(0, width - used - (label.Length > 0 ? 1 : 0)));
        AddStr(TextLayout.Truncate(rest, Math.Max(0, width - used)));
        return true;
    }
}
