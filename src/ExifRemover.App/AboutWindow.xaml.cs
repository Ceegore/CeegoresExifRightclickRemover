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
        // D89 (M2.20.28): the pre-fix code had a bare `catch { }` that
        // silently swallowed any exception from Process.Start. This is
        // the same R17-3 pattern that R17 of the SteamReviewTool audit
        // found for silent error swallows on user-facing paths: a
        // user-triggered action (clicking a hyperlink) that fails
        // silently leaves the user with no feedback — they don't know
        // whether the link worked, whether they have a default browser,
        // or whether their AV blocked the launch. Fix: catch the
        // specific exception class, show a MessageBox with the error
        // message and a hint. The user gets actionable feedback
        // ("check that you have a default browser configured") instead
        // of wondering why nothing happened.
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            // The most common failure is "no default browser" — the
            // user gets a Win32Exception with a message like "No
            // application is associated with the specified file for
            // this operation." The hint helps the user diagnose the
            // most likely cause. Other exceptions (e.g. malformed URI)
            // get the same MessageBox so the user knows the click
            // didn't silently do nothing.
            MessageBox.Show(
                this,
                $"Could not open the link:\n\n{ex.Message}\n\nIf you don't have a default browser configured, set one in Windows Settings → Apps → Default apps.",
                "ExifRemover",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        e.Handled = true;
    }
}