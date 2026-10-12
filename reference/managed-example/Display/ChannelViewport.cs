using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace HmmExample.Display;

public sealed class ChannelViewport : Grid
{
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(
        nameof(Label), typeof(string), typeof(ChannelViewport), new PropertyMetadata("", (d, e) => ((ChannelViewport)d).label.Text = (string)e.NewValue));
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public static readonly DependencyProperty RawCaptionProperty=DependencyProperty.Register(nameof(RawCaption),typeof(string),typeof(ChannelViewport),
        new PropertyMetadata("RAW FRAME - includes Dummy/Overscan",(d,e)=>((ChannelViewport)d).rawLabel.Text=(string)e.NewValue));
    public string RawCaption {get=>(string)GetValue(RawCaptionProperty);set=>SetValue(RawCaptionProperty,value);}
    private readonly TextBlock label = new() { FontSize = 18, Margin = new Thickness(10), Foreground = Brushes.White, Background = Brushes.Black,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly TextBlock placeholder = new() { Text = "Awaiting image data", Foreground = Brushes.SlateGray,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock rawLabel = new() { Text = "RAW FRAME - includes Dummy/Overscan", Foreground = Brushes.White,
        Background = Brushes.Black, FontSize = 11, Margin = new Thickness(6), TextWrapping = TextWrapping.Wrap,
        HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Visibility = Visibility.Collapsed };
    internal Image Image { get; } = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Stretch };
    public ChannelViewport()
    {
        Background = Brushes.Black;
        ClipToBounds = true;
        Children.Add(Image); Children.Add(placeholder); Children.Add(label); Children.Add(rawLabel);
    }
    internal void Present(ImageSource source, bool raw = false) { Image.Source = source; placeholder.Visibility = Visibility.Collapsed; rawLabel.Visibility = raw ? Visibility.Visible : Visibility.Collapsed; }
    internal void ShowStatus(string text) {Image.Source=null;placeholder.Text=text;placeholder.TextWrapping=TextWrapping.Wrap;placeholder.Margin=new Thickness(12);placeholder.Visibility=Visibility.Visible;rawLabel.Visibility=Visibility.Visible;}
}
