using System.Windows;
using System.Windows.Controls;

namespace HmmExample.Display;

public sealed class SquareChannelPanel : Panel
{
    private const double Gap = 12;
    private static double Side(Size size) => Math.Max(0, (Math.Min(size.Width, size.Height) - Gap) / 2);
    protected override Size MeasureOverride(Size availableSize)
    {
        double side = Side(availableSize);
        if (!double.IsFinite(side)) side = 240;
        foreach (UIElement child in InternalChildren) child.Measure(new Size(side, side));
        return new Size(side * 2 + Gap, side * 2 + Gap);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double side = Side(finalSize);
        double x = (finalSize.Width - (side * 2 + Gap)) / 2;
        double y = (finalSize.Height - (side * 2 + Gap)) / 2;
        for (int i = 0; i < InternalChildren.Count; i++)
            InternalChildren[i].Arrange(new Rect(x + (i % 2) * (side + Gap), y + (i / 2) * (side + Gap), side, side));
        return finalSize;
    }
}
