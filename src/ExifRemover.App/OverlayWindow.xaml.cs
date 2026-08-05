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

        // D10: BaseSubtitle drives the bound SubtitleText TextBlock, so any non-fatal
        // notice already set on the VM is automatically prepended. The previous code
        // set SubtitleText.Text directly here, which overwrote the notice that
        // SetNonFatalNotice had just put there (the notice was visible for only the
        // few ms between main.Show() and this handler firing).
        _vm.BaseSubtitle = _vm.HasMultipleFiles
            ? $"{_vm.Files.Count} files selected. Review metadata below, then click Remove to strip all."
            : "Review metadata below, then click Remove.";

        // D11: disable Remove/Cancel while the initial inspect is running so a
        // premature click can't race the snapshot capture. The CapturePreStripSnapshots
        // call would otherwise see FileEntryViewModel.Inspection == null for every
        // file and produce an empty snapshot, leaving the post-strip grid showing
        // "0 entries removed" with no way to know what was actually in the files.
        // D94: collapsed to the SetBusyState helper — every other multi-button
        // busy/idle transition in this file goes through the same helper, so this
        // site is the single canonical entry point.
        SetBusyState(true);
        _vm.IsBusy = true;
        // D5: wrap the inspect call in a try/catch and surface any thrown exception on
        // the UI thread. The previous Task.Run() did not observe exceptions, so if
        // _vm.InspectAll() ever threw (the MetadataInspector already catches internally
        // and returns an Error-bearing FileInspection, so this is currently theoretical —
        // but a future MetadataExtractor version could throw, and the unobserved-task
        // exception would silently leave IsBusy=true with the grid empty).
        // D15: every Dispatcher.Invoke in this Task.Run is wrapped in SafeInvoke, which
        // no-ops if the dispatcher has begun shutdown (the user closed the window
        // mid-inspect). The strip itself is unaffected — the source file is only
        // written in RunRemove, and the catch/finally of the stripper handles partial
        // writes — so a mid-inspect window close is a clean "user changed their mind",
        // not a data-loss scenario.
        Task.Run(() =>
        {
            try
            {
                _vm.InspectAll();
            }
            catch (Exception ex)
            {
                SafeInvoke(() =>
                {
                    _vm.IsBusy = false;
                    _vm.StatusText = $"Could not inspect files: {ex.Message}";
                    SetBusyState(false);
                });
                return;
            }
            SafeInvoke(() =>
            {
                _vm.IsBusy = false;
                SetBusyState(false);
            });
        });
    }

    /// <summary>
    /// D15: invoke <paramref name="action"/> on the UI thread, but only if the dispatcher
    /// is still alive. If the window has been closed (the user clicked X, or the app is
    /// shutting down), <c>Dispatcher.Invoke</c> throws an <c>OperationCanceledException</c>
    /// (specifically <c>TaskCanceledException</c> today) and the inner <see cref="Task"/>
    /// faults — producing an unobserved-task-exception log entry and, with the wrong
    /// runtime config, a process crash. None of the post-strip UI updates are essential
    /// (the file is already written; the user has already left), so the safe behaviour
    /// is to no-op when the window is going away.
    /// </summary>
    private void SafeInvoke(Action action)
    {
        if (Dispatcher.HasShutdownStarted) return;
        try
        {
            Dispatcher.Invoke(action);
        }
        catch (OperationCanceledException)
        {
            // D46: catch the base class, not just TaskCanceledException. WPF's
            // Dispatcher.Invoke currently throws TaskCanceledException (which derives
            // from OperationCanceledException), but a future WPF version could throw
            // a different subclass. Catching the base class is forward-compatible and
            // still lets genuinely-unexpected exceptions propagate.
        }
    }

    /// <summary>
    /// D94: enable/disable the three action buttons (Remove, Cancel, ReInspect) as a
    /// single unit. Before this helper existed, every busy/idle transition in the
    /// file hand-rolled the same 3-line block:
    /// <code>
    ///     RemoveButton.IsEnabled = ...;
    ///     CancelButton.IsEnabled = ...;
    ///     ReInspectButton.IsEnabled = ...;
    /// </code>
    /// 7 call sites (3 disable + 4 enable) = 21 identical lines. Forgetting one of
    /// the three buttons at a new transition site is a silent UX bug (e.g. the
    /// Remove button stays enabled mid-inspect, racing the snapshot capture that
    /// D11 protects against). Funneling every transition through this helper makes
    /// the contract explicit: there is exactly one way to change the busy/idle
    /// state of the action bar, and any future audit round can verify the count
    /// with a source-shape regression test.
    ///
    /// <para>
    /// This helper deliberately does NOT touch <c>_vm.IsBusy</c> — the progress bar
    /// bound to that property is a separate concern (it shows during a real
    /// operation but should NOT show over a fatal error message). Callers that
    /// are starting/ending a real operation set <c>_vm.IsBusy</c> on the line
    /// before or after the helper call.
    /// </para>
    /// </summary>
    /// <param name="busy">
    /// <c>true</c> to disable all three action buttons (a real operation is in
    /// flight, or the window is no longer interactive); <c>false</c> to re-enable
    /// them (operation completed or failed cleanly).
    /// </param>
    private void SetBusyState(bool busy)
    {
        RemoveButton.IsEnabled = !busy;
        CancelButton.IsEnabled = !busy;
        ReInspectButton.IsEnabled = !busy;
    }

    private void ShowFatal(string message)
    {
        // D10: BaseSubtitle drives the bound SubtitleText, and StatusText is the VM-bound
        // property — both will be displayed correctly without manual Text assignment.
        _vm.BaseSubtitle = message;
        _vm.StatusText = message;
        // D94: collapse the 3-button disable into the helper. Note we do NOT
        // set _vm.IsBusy = true here: that would show the progress bar over the
        // error message, which is the wrong UX. The 3 buttons being disabled is
        // the user-facing signal that the window is no longer interactive.
        SetBusyState(true);
    }

    /// <summary>
    /// Displays a non-fatal notice (e.g. "ignored 3 unsupported files") in both the
    /// status strip and the subtitle, without disabling Remove. The notice is
    /// persistent (D9/D10 fix): once set, the VM's StatusText and SubtitleText
    /// getters always prepend it, so subsequent status updates (inspect counts,
    /// progress messages, summaries) don't clobber the notice.
    /// </summary>
    public void SetNonFatalNotice(string notice)
    {
        if (string.IsNullOrEmpty(notice)) return;
        // Set the VM property; the bound TextBlocks pick it up via PropertyChanged.
        // The VM's StatusText and SubtitleText getters always prepend the notice, so
        // the notice persists across every later status update (inspect counts,
        // progress messages, post-strip summaries) — D9 fix.
        _vm.NonFatalNotice = notice;
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

    /// <summary>
    /// D14: re-inspect all files. The pre-strip snapshot (set after a Remove) is cleared
    /// so the grid switches back to "live" entries — useful for confirming the post-strip
    /// state (a successful strip makes every entry say "Would be removed", which is the
    /// exact opposite of what a re-inspect shows: "no metadata in this file").
    /// </summary>
    private void ReInspectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.IsBusy) return;
        ReInspectButton.IsEnabled = false;
        _vm.IsBusy = true;
        Task.Run(() =>
        {
            try
            {
                _vm.Reinspect();
            }
            catch (Exception ex)
            {
                // D15: SafeInvoke no-ops if the window is closing. The re-inspect is
                // a read-only operation; the worst case if we get here is a missing
                // status message, not data loss.
                SafeInvoke(() =>
                {
                    _vm.StatusText = $"Could not re-inspect: {ex.Message}";
                });
            }
            SafeInvoke(() =>
            {
                _vm.IsBusy = false;
                ReInspectButton.IsEnabled = true;
            });
        });
    }

    private void CopyValue_Click(object sender, RoutedEventArgs e)
    {
        if (EntriesGrid.SelectedItem is not EntryRow row) return;
        // D13: Clipboard.SetText can throw COMException if another process holds the
        // clipboard (rare, but possible — e.g. another app is mid-paste). Surface
        // the error in the status strip rather than letting it crash the overlay.
        try
        {
            Clipboard.SetText(row.Value ?? string.Empty);
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Could not copy to clipboard: {ex.Message}";
        }
    }

    private void CopyRow_Click(object sender, RoutedEventArgs e)
    {
        if (EntriesGrid.SelectedItem is not EntryRow row) return;
        // D13: see CopyValue_Click — clipboard can throw COMException.
        try
        {
            Clipboard.SetText($"{row.Group}\t{row.Name}\t{row.Value}");
        }
        catch (Exception ex)
        {
            _vm.StatusText = $"Could not copy to clipboard: {ex.Message}";
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

        // Snapshot the per-file inspection state BEFORE the strip wipes the in-memory
        // entries. After the strip, RebuildCurrentEntries renders the snapshot rows
        // (all marked "Would be removed") so the user can see what was actually taken out.
        _vm.CapturePreStripSnapshots();

        // D94: collapse the 3-button disable into the helper.
        SetBusyState(true);
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
                // D15: SafeInvoke no-ops if the window has been closed mid-strip. The
                // stripper's own catch already deleted the temp output, so the source
                // is safe; we just lose the user-facing error message.
                SafeInvoke(() =>
                {
                    _vm.IsBusy = false;
                    _vm.StatusText = $"Strip failed: {ex.Message}";
                    // D94: collapse the 3-button enable into the helper.
                    SetBusyState(false);
                });
                return;
            }

            // D15: same guard for the success path. The stripped files are already
            // on disk by the time we get here (atomic File.Replace / sibling write);
            // a missing summary message is the only consequence of the window having
            // closed during the strip.
            SafeInvoke(() =>
            {
                _vm.IsBusy = false;
                _vm.ProgressValue = 1.0;
                ShowSummary(report);

                // Re-enable the controls so the window is usable again after a strip.
                // D94: collapse the 3-button enable into the helper. The pre-strip
                // snapshot (captured in RunRemove before the strip) is rendered by
                // RebuildCurrentEntries until the user explicitly re-inspects a file.
                // The "↻" button in the header (D14 fix) clears the snapshot and
                // re-inspects so the user can confirm the post-strip state.
                SetBusyState(false);
            });
        });
    }

    private void ShowSummary(BatchStripReport report)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append($"Removed metadata from {report.ChangedCount} of {report.Results.Count} files; saved ");
        sb.Append(Formatting.FormatBytes(report.TotalSavedBytes));
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
}