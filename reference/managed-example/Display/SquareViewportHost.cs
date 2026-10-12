using System.Windows;
using System.Windows.Controls;

namespace HmmExample.Display;

public sealed class SquareViewportHost : Decorator
{
    protected override Size MeasureOverride(Size availableSize)
    {
        double side=Math.Min(availableSize.Width,availableSize.Height);
        if(!double.IsFinite(side)) side=240;
        side=Math.Max(0,side);Child?.Measure(new(side,side));return new(side,side);
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        double side=Math.Max(0,Math.Min(finalSize.Width,finalSize.Height));
        Child?.Arrange(new Rect((finalSize.Width-side)/2,(finalSize.Height-side)/2,side,side));return finalSize;
    }
}
