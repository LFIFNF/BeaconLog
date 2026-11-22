using System.Windows;
using System.Windows.Controls;

namespace BeaconLog;

public partial class StatusBallColorPicker : UserControl
{
    public StatusBallColorPicker()
    {
        InitializeComponent();
    }

    public string ColorText
    {
        get => HexInput.Text;
        set => HexInput.Text = value;
    }

    private void Palette_Click(object sender, RoutedEventArgs e)
    {
        ColorText = (string)((Button)sender).Tag;
    }
}
