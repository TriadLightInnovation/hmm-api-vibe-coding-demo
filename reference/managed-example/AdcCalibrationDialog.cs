using System.Windows;
using System.Windows.Controls;
namespace HmmExample;
internal sealed class AdcCalibrationDialog : Window
{
    internal AdcCalibrationDialog()
    {
        Title="ADC Calibration"; Width=530; SizeToContent=SizeToContent.Height;
        ResizeMode=ResizeMode.NoResize; WindowStartupLocation=WindowStartupLocation.CenterOwner;
        var panel=new StackPanel {Margin=new Thickness(24),Background=System.Windows.Media.Brushes.White};
        panel.Children.Add(new TextBlock {Text="Ensure detector/ADC inputs are in the intended calibration condition\nand acquisition is stopped.\n\nProceed?",TextWrapping=TextWrapping.Wrap});
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right,Margin=new Thickness(0,24,0,0)};
        var cancel=new Button {Content="Cancel",IsCancel=true,IsDefault=true,MinWidth=90,Padding=new Thickness(12,6,12,6),Margin=new Thickness(0,0,8,0)};
        cancel.Click+=(_,_)=>DialogResult=false;
        var calibrate=new Button {Content="Calibrate",MinWidth=90,Padding=new Thickness(12,6,12,6)};
        calibrate.Click+=(_,_)=>DialogResult=true;
        buttons.Children.Add(cancel);buttons.Children.Add(calibrate);panel.Children.Add(buttons);Content=panel;
    }
}
