using System.Diagnostics;
using System.Reflection;
using System.Windows;

namespace ExifRemover.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var assembly = typeof(App).Assembly;
        var version = assembly.GetName().Version?.ToString() ?? "1.0.0";
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? version;
        VersionText.Text = $"Version {informational}";
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void Hyperlink_RequestNavigate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
        }
        e.Handled = true;
    }
}