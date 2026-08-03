using System.Windows;

namespace ExifRemover.App;

public partial class ConfirmWindow : Window
{
    public bool Confirmed { get; private set; }

    public ConfirmWindow(IReadOnlyList<string> paths, ExifRemover.Engine.StripProfile profile, bool overwrite)
    {
        InitializeComponent();
        var desc = ExifRemover.Engine.StripProfileCatalog.Describe(profile);
        ProfileSummaryText.Text = $"Profile: {desc.Title}. Output: {(overwrite ? "overwrite source files in place" : "write '<name>_stripped.<ext>' beside each source")}.";
        foreach (var p in paths)
        {
            FileList.Items.Add(p);
        }
    }

    public bool DontAskAgainSession => DontAskAgainCheck.IsChecked == true;

    private void Yes_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        Close();
    }

    private void No_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = false;
        Close();
    }
}