using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ExifRemover.Engine;

namespace ExifRemover.App;

public partial class OverlayWindow : Window
{
    private readonly OverlayViewModel _vm;
    private static bool _sessionDontAsk = false;

    public OverlayWindow(IReadOnlyList<string> paths)
    {
        InitializeComponent();
        if (paths.Count == 0)
        {
            _vm = new OverlayViewModel(Array.Empty<string>(), Dispatcher);
            ShowFatal("No supported images were selected. ExifRemover supports .jpg, .jpeg, and .png files.");
            return;
        }
        _vm = new OverlayViewModel(paths, Dispatcher);
        DataContext = _vm;

        // Esc closes the overlay (Enter triggers Remove via the button's IsDefault).
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                Close();
            }
        };

        Loaded += OverlayWindow_Loaded;
    }

    private void OverlayWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_vm.Files.Count == 0)
        {
            ShowFatal("None of the selected files are supported. ExifRemover supports .jpg, .jpeg, and .png.");
            return;
        }

        SubtitleText.Text = _vm.HasMultipleFiles
            ? $"{_vm.Files.Count} files selected. Review metadata below, then click Remove to strip all."
            : "Review metadata below, then click Remove.";

        _vm.IsBusy = true;
        Task.Run(() =>
        {
            _vm.InspectAll();
            Dispatcher.Invoke(() => _vm.IsBusy = false);
        });
    }

    private void ShowFatal(string message)
    {
        SubtitleText.Text = message;
        StatusText.Text = message;
        RemoveButton.IsEnabled = false;
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void AboutButton_Click(object sender, RoutedEventArgs e)
    {
        var about = new AboutWindow { Owner = this };
        about.ShowDialog();
    }

    private void CopyValue_Click(object sender, RoutedEventArgs e)
    {
        if (EntriesGrid.SelectedItem is EntryRow row)
        {
            Clipboard.SetText(row.Value ?? string.Empty);
        }
    }

    private void CopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (EntriesGrid.SelectedItem is EntryRow row)
        {
            Clipboard.SetText($"{row.Group}\t{row.Name}\t{row.Value}");
        }
    }

    private void RemoveButton_Click(object sender, RoutedEventArgs e)
    {
        RunRemove();
    }

    private void RunRemove()
    {
        if (_vm.SelectedProfile is null) return;
        var profile = _vm.SelectedProfile.Profile;
        App.OverlayProfile = profile;
        var overwrite = _vm.OverwriteSource;
        var paths = _vm.Files.Select(f => f.Path).ToList();

        if (paths.Count > 1 && !_sessionDontAsk)
        {
            var confirm = new ConfirmWindow(paths, profile, overwrite) { Owner = this };
            confirm.ShowDialog();
            if (!confirm.Confirmed) return;
            if (confirm.DontAskAgainSession) _sessionDontAsk = true;
        }

        RemoveButton.IsEnabled = false;
        CancelButton.IsEnabled = false;
        _vm.IsBusy = true;
        _vm.ProgressValue = 0;

        var progress = new Progress<(int Done, int Total, string CurrentFile)>(p =>
        {
            _vm.ProgressValue = p.Total == 0 ? 1.0 : (double)p.Done / p.Total;
            _vm.StatusText = $"Stripping {p.Done}/{p.Total}: {p.CurrentFile}";
        });

        Task.Run(() =>
        {
            BatchStripReport report;
            try
            {
                report = StripPipeline.StripBatch(paths, overwrite, profile, progress);
            }
            catch (Exception ex)
            {
                Dispatcher.Invoke(() =>
                {
                    _vm.IsBusy = false;
                    _vm.StatusText = $"Strip failed: {ex.Message}";
                    RemoveButton.IsEnabled = true;
                    CancelButton.IsEnabled = true;
                });
                return;
            }

            Dispatcher.Invoke(() =>
            {
                _vm.IsBusy = false;
                _vm.ProgressValue = 1.0;
                ShowSummary(report);

                // Re-enable the controls so the window is usable again after a strip.
                RemoveButton.IsEnabled = true;
                CancelButton.IsEnabled = true;
                // The pre-strip snapshot (captured in RunRemove before the strip) is
                // rendered by RebuildCurrentEntries until the user explicitly re-inspects
                // a file. No re-inspection here — the freshly stripped files are now
                // empty and would render as "no metadata", which is what the user already
                // saw. The snapshot gives them a meaningful summary of what was removed.
            });
        });
    }

    private void ShowSummary(BatchStripReport report)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"Removed metadata from {report.ChangedCount} of {report.Results.Count} files; saved ");
        sb.Append(FormatBytes(report.TotalSavedBytes));
        sb.Append('.');
        if (report.Failures.Count > 0)
        {
            sb.Append($" {report.Failures.Count} file(s) failed:");
            foreach (var (path, err) in report.Failures.Take(3))
            {
                sb.Append($"\n - {System.IO.Path.GetFileName(path)}: {err}");
            }
            if (report.Failures.Count > 3) sb.Append($"\n - … and {report.Failures.Count - 3} more.");
        }
        _vm.StatusText = sb.ToString();
    }

    private static string FormatBytes(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return $"{b / 1024.0:0.0} KB";
        return $"{b / 1024.0 / 1024.0:0.00} MB";
    }
}