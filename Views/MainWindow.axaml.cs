using Avalonia.Controls;
using Avalonia.Interactivity;

namespace QrOverlayScanner.Views;

public partial class MainWindow : Window
{
    private TextBox ResultBox => this.FindControl<TextBox>("ResultTextBox")!;
    private Button ScanButtonControl => this.FindControl<Button>("ScanButton")!;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void ScanButton_OnClick(object? sender, RoutedEventArgs e)
    {
        ScanButtonControl.IsEnabled = false;
        try
        {
            var scanner = new ScannerWindow();
            var result = await scanner.ShowDialog<string?>(this);
            if (result is not null)
                ResultBox.Text = result;
        }
        finally
        {
            ScanButtonControl.IsEnabled = true;
        }
    }
}
