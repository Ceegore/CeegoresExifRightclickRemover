using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using ExifRemover.Engine;

namespace ExifRemover.App;

public sealed class OverlayViewModel : INotifyPropertyChanged
{
    private readonly Dispatcher _dispatcher;
    private readonly ObservableCollection<FileEntryViewModel> _files = new();

    public ObservableCollection<FileEntryViewModel> Files => _files;
    public ICollectionView FilesView => CollectionViewSource.GetDefaultView(_files);
    public ObservableCollection<StripProfileOption> Profiles { get; } = new();
    public ObservableCollection<object> AllEntries { get; } = new();

    public ICollectionView EntriesView { get; }

    public OverlayViewModel(IReadOnlyList<string> paths, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        // D85 (M2.20.26): the pre-fix code copied `paths` into a private
        // `_allPaths` field via `paths.ToList()` and then iterated `_allPaths`
        // in the constructor. The field was never read anywhere else — the
        // only consumer was the loop two lines below, which can iterate
        // `paths` directly (it's already a `IReadOnlyList<string>`). The
        // `.ToList()` was also unnecessary work: it allocated a new List
        // for every OverlayViewModel instance and added GC pressure. The
        // field has been removed; the loop iterates the parameter directly.
        // The pattern is the same R17-2 (dead field) finding that D82 caught
        // for `StripResult.Warning`: a private field that survived multiple
        // audit rounds because it was never exercised outside the
        // constructor.
        //
        // D88 (M2.20.27): the pre-fix code also declared `_byPath` as a
        // private field, used only in the constructor for the D78
        // case-insensitive dedup. The field is dead after construction
        // (never read by the property accessors, the strip pipeline, or
        // the UI bindings — only the constructor's `ContainsKey` /
        // indexer-set use it). Same R17-2 pattern as `_allPaths` /
        // `StripResult.Warning`. Moved to a local `seen` dictionary.
        // The `OrdinalIgnoreCase` comparer is preserved (Windows path
        // semantics: "FOO.jpg" and "foo.jpg" refer to the same file).
        var seen = new Dictionary<string, FileEntryViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in paths)
        {
            if (!File.Exists(p))
            {
                continue;
            }
            // D78: deduplicate paths case-insensitively (Windows path semantics).
            // The pre-fix code added a FileEntryViewModel for every path entry,
            // even when two entries referred to the same file (e.g. "foo.jpg" and
            // "FOO.jpg" from a multi-select that dragged the same file twice, or
            // a hand-typed pair in the registry). The result was the same file
            // appearing twice in the ComboBox, and the stripper processing it
            // twice — the second Strip call would either fail (source modified
            // between calls) or produce a duplicate "_stripped (2)" sibling.
            // `seen` (D88) already uses OrdinalIgnoreCase, so a single
            // ContainsKey check is enough to dedupe both case-different and
            // exact duplicates.
            if (seen.ContainsKey(p))
            {
                continue;
            }
            var vm = new FileEntryViewModel(p);
            // When a file's Inspection completes, refresh entries if it's the currently
            // selected file (this is what populates the table after the initial async inspect).
            vm.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(FileEntryViewModel.Inspection)
                    && ReferenceEquals(SelectedFile, s))
                {
                    RebuildCurrentEntries();
                }
            };
            _files.Add(vm);
            seen[p] = vm;
        }

        foreach (StripProfile profile in Enum.GetValues<StripProfile>())
        {
            Profiles.Add(new StripProfileOption(profile));
        }
        SelectedProfile = Profiles.FirstOrDefault(p => p.Profile == StripProfile.Privacy) ?? Profiles[0];

        EntriesView = CollectionViewSource.GetDefaultView(AllEntries);
        EntriesView.Filter = EntryFilter;

        if (_files.Count > 0)
        {
            SelectedFile = _files[0];
        }
    }

    public bool HasMultipleFiles => _files.Count > 1;

    private StripProfileOption? _selectedProfile;
    public StripProfileOption? SelectedProfile
    {
        get => _selectedProfile;
        set
        {
            if (_selectedProfile == value) return;
            _selectedProfile = value;
            // Sync to App.OverlayProfile (used by RunRemove).
            if (value is not null) App.OverlayProfile = value.Profile;
            OnPropertyChanged();
            OnPropertyChanged(nameof(ProfileLongDescription));
            RecomputeKeepSets();
            RebuildCurrentEntries();
        }
    }

    private void RecomputeKeepSets()
    {
        var profile = _selectedProfile?.Profile ?? StripProfile.Privacy;
        foreach (var f in _files)
        {
            f.KeepSet = ComputeKeepSet(f.Inspection?.Format, profile);
            f.OnPropertyChanged(nameof(f.KeepSet));
        }
    }

    private static HashSet<string> ComputeKeepSet(ImageFormat? format, StripProfile profile)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        // D51: fail-safe default for any entry that falls through to the "Other" group
        // (MetadataGroups.Other = "Other"). MapGroup's _ => dir.Name ?? MetadataGroups.Other
        // fallback fires when MetadataExtractor surfaces a directory that doesn't match any
        // of the explicit cases. The stripper operates on bytes, not on MetadataExtractor's
        // directory abstraction, so we can't be 100% sure the stripper drops the underlying
        // bytes — marking "Other" as kept is the safe default. If we don't know what the
        // directory represents, don't claim the stripper will remove it. (Same fail-safe
        // reasoning as the "PNGUNKNOWN" entry for the PNG path.)
        set.Add("Other");
        if (format == ImageFormat.Jpeg)
        {
            set.Add("JFIF");
            // ICC is kept only under Minimal; Privacy and AllMetadata both strip it
            // (must match JpegMetadataStripper, where keepIcc == (profile == Minimal)).
            if (profile == StripProfile.Minimal)
            {
                set.Add("ICC");
            }
        }
        else if (format == ImageFormat.Png)
        {
            // Chunks the stripper ALWAYS keeps regardless of profile (must mirror
            // PngMetadataStripper.ShouldDrop, which never returns true for these types).
            set.Add("PNGPHYS");
            set.Add("PNGBKGD");
            set.Add("PNGSBIT");
            set.Add("PNGTRNS");

            // D2: any chunk MetadataExtractor surfaces as "PNG Unknown" (e.g. a newer
            // PngDirectory tag that doesn't match a known case in MapPngGroup, or a
            // custom ancillary chunk) is kept by the stripper — PngMetadataStripper.ShouldDrop
            // only returns true for tEXt/zTXt/iTXt/tIME/eXIf/iCCP/hIST/gAMA/cHRM/sRGB, and
            // falls through to "return false" (keep) for anything else. The grid must
            // match that contract: an unknown ancillary chunk must show as "Would be kept",
            // never "Would be removed" (H2 lie).
            set.Add("PNGUNKNOWN");

            // Color-management chunks: kept under Privacy/Minimal, stripped under AllMetadata.
            if (profile != StripProfile.AllMetadata)
            {
                set.Add("PNGSRGB");
                set.Add("PNGCHRM");
                set.Add("PNGGAMA");
            }

            // iCCP and hIST: kept only under Minimal.
            if (profile == StripProfile.Minimal)
            {
                set.Add("PNGICCP");
                set.Add("PNGHIST");
            }
        }
        return set;
    }

    public string ProfileLongDescription => SelectedProfile is null
        ? string.Empty
        : SelectedProfile.LongDescription;

    private FileEntryViewModel? _selectedFile;
    public FileEntryViewModel? SelectedFile
    {
        get => _selectedFile;
        set
        {
            if (_selectedFile == value) return;
            _selectedFile = value;
            OnPropertyChanged();
            // Recompute the keep-set for the new file with the current profile.
            if (value is not null)
            {
                value.KeepSet = ComputeKeepSet(value.Inspection?.Format, _selectedProfile?.Profile ?? StripProfile.Privacy);
            }
            RebuildCurrentEntries();
        }
    }

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set
        {
            if (_filterText == value) return;
            _filterText = value;
            OnPropertyChanged();
            EntriesView.Refresh();
            OnPropertyChanged(nameof(VisibleEntryCount));
            // D36: VisibleEntryCount is the value the StatusText string contains
            // ("{VisibleEntryCount} of {total} entries shown"). The filter changes
            // the visible count but the bound StatusText string is only re-composed
            // by UpdateStatusFromEntries — which is called from RebuildCurrentEntries
            // and RunRemove but NOT from the filter path. Without this call the user
            // types a filter, the grid re-renders to 3 rows, but the status strip
            // still reads "5 of 5 entries shown" (the last pre-filter count). The
            // Setter here reuses the same status-format logic so the two views
            // (grid and status) agree at every keystroke.
            UpdateStatusFromEntries();
        }
    }

    public int VisibleEntryCount => AllEntries.Count(o => EntryFilter(o));

    private bool _overwriteSource;
    public bool OverwriteSource
    {
        get => _overwriteSource;
        set
        {
            if (_overwriteSource == value) return;
            _overwriteSource = value;
            OnPropertyChanged();
        }
    }

    private string _statusText = string.Empty;
    public string StatusText
    {
        // D9: the getter returns the "base" status with the non-fatal notice prepended
        // (when one is set). The setter stores only the base; the full value is
        // computed here so the XAML binding always reflects the current notice. The
        // previous code modified XAML StatusText.Text directly from the window code,
        // which the next VM update immediately overwrote — making the dropped-files
        // notice invisible to the user in practice.
        get => string.IsNullOrEmpty(_nonFatalNotice) ? _statusText : $"{_nonFatalNotice}  •  {_statusText}";
        set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Optional, persistent non-fatal notice (e.g. "ignored 3 unsupported files").
    /// When set, it is prepended to both the status text and the subtitle text and
    /// stays visible for the entire overlay session. Cleared by re-applying the
    /// empty string from the caller if needed; the window code treats it as
    /// "one-shot, sticky" — D9/D10 fix the previous behavior where the notice
    /// was overwritten by the very next VM update.
    /// </summary>
    private string _nonFatalNotice = string.Empty;
    public string NonFatalNotice
    {
        get => _nonFatalNotice;
        set
        {
            if (_nonFatalNotice == value) return;
            _nonFatalNotice = value;
            OnPropertyChanged(nameof(StatusText));
            OnPropertyChanged(nameof(SubtitleText));
        }
    }

    /// <summary>
    /// Subtitle text shown at the top of the overlay. Composed of the (optional)
    /// non-fatal notice prepended to the base subtitle — D10: previously set as a
    /// raw XAML TextBlock from the window code, which the Loaded handler then
    /// overwrote, making the notice invisible.
    /// </summary>
    public string SubtitleText => string.IsNullOrEmpty(_nonFatalNotice)
        ? _baseSubtitle
        : string.IsNullOrEmpty(_baseSubtitle)
            ? _nonFatalNotice
            : $"{_nonFatalNotice}  •  {_baseSubtitle}";

    private string _baseSubtitle = "Inspecting metadata...";
    public string BaseSubtitle
    {
        get => _baseSubtitle;
        set
        {
            if (_baseSubtitle == value) return;
            _baseSubtitle = value;
            OnPropertyChanged(nameof(SubtitleText));
        }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
        }
    }

    private double _progressValue;
    public double ProgressValue
    {
        get => _progressValue;
        set
        {
            if (Math.Abs(_progressValue - value) < 0.0001) return;
            _progressValue = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// Per-file snapshot of metadata entries BEFORE a strip completed. Populated by
    /// RunRemove before invoking the stripper. While the snapshot is present, the entry
    /// grid renders those entries (with all marked "Removed") instead of the post-strip
    /// inspection, so the user can see what was actually removed.
    /// </summary>
    public Dictionary<string, IReadOnlyList<MetadataEntry>> PreStripSnapshots { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    public void CapturePreStripSnapshots()
    {
        PreStripSnapshots.Clear();
        foreach (var f in _files)
        {
            if (f.Inspection is { Error: null } inspection && inspection.Entries.Count > 0)
            {
                PreStripSnapshots[f.Path] = inspection.Entries.ToList();
            }
        }
    }

    public void ClearPreStripSnapshots()
    {
        if (PreStripSnapshots.Count == 0) return;
        PreStripSnapshots.Clear();
    }

    /// <summary>
    /// D14: re-inspect all files. Clears the pre-strip snapshot (so the grid switches
    /// back to "live" entries), then re-runs the inspect. Used after a strip so the
    /// user can confirm the post-strip state ("now empty" for a successful strip) or
    /// refresh after a file was changed externally. Safe to call any time; the actual
    /// I/O runs off-thread via the same InspectAll path as the initial load.
    /// </summary>
    public void Reinspect()
    {
        ClearPreStripSnapshots();
        InspectAll();
    }

    public void InspectAll()
    {
        // Slow part: read metadata off whatever thread we are on (callers run this in a Task).
        foreach (var f in _files)
        {
            f.InspectData();
        }

        // UI part: all PropertyChanged events and ObservableCollection mutations must happen on
        // the dispatcher thread, otherwise WPF throws NotSupportedException for the bound views.
        void ApplyUi()
        {
            foreach (var f in _files)
            {
                f.NotifyInspected();
            }
            RecomputeKeepSets();
            RebuildCurrentEntries();
            UpdateStatusFromEntries();
        }

        if (_dispatcher.CheckAccess())
        {
            ApplyUi();
        }
        else
        {
            _dispatcher.Invoke(ApplyUi);
        }
    }

    private void UpdateStatusFromEntries()
    {
        if (SelectedFile is null)
        {
            StatusText = string.Empty;
            return;
        }
        // If a pre-strip snapshot is active, the snapshot is the source of truth.
        if (PreStripSnapshots.TryGetValue(SelectedFile.Path, out var snap))
        {
            StatusText = snap.Count == 0
                ? "No metadata in this file."
                : $"{VisibleEntryCount} of {snap.Count} entries shown (last strip removed all).";
            return;
        }
        var total = SelectedFile.Inspection?.Entries.Count ?? 0;
        StatusText = total == 0
            ? "No metadata in this file."
            : $"{VisibleEntryCount} of {total} entries shown.";
    }

    private void RebuildCurrentEntries()
    {
        AllEntries.Clear();
        if (SelectedFile is null) return;

        // If we have a pre-strip snapshot for the selected file (set after a Remove),
        // show those entries with all marked "Removed" — that's the value the user just
        // produced. The post-strip Inspection is empty for a successful strip.
        if (PreStripSnapshots.TryGetValue(SelectedFile.Path, out var snap) && snap.Count > 0)
        {
            foreach (var entry in snap)
            {
                AllEntries.Add(new EntryRow(entry, wouldKeep: false));
            }
            UpdateStatusFromEntries();
            return;
        }

        if (SelectedFile.Inspection is null) return;

        var entries = SelectedFile.Inspection.Entries;
        // Ensure KeepSet reflects the current profile (defensive — normally set by RecomputeKeepSets).
        SelectedFile.KeepSet = ComputeKeepSet(SelectedFile.Inspection.Format, _selectedProfile?.Profile ?? StripProfile.Privacy);
        var keep = SelectedFile.KeepSet;

        foreach (var entry in entries)
        {
            AllEntries.Add(new EntryRow(entry, wouldKeep: keep.Contains(GetChunkKey(entry))));
        }

        UpdateStatusFromEntries();
    }

    private bool EntryFilter(object obj)
    {
        if (obj is not EntryRow row) return false;
        if (string.IsNullOrEmpty(_filterText)) return true;
        return (row.Entry.Name?.Contains(_filterText, StringComparison.OrdinalIgnoreCase) ?? false)
            || (row.Entry.Value?.Contains(_filterText, StringComparison.OrdinalIgnoreCase) ?? false)
            || (row.Entry.Group?.Contains(_filterText, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string GetChunkKey(MetadataEntry entry) => KeepSetKey.For(entry);

    public event PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class FileEntryViewModel : INotifyPropertyChanged
{
    public string Path { get; }
    public string DisplayName => System.IO.Path.GetFileName(Path);
    public string Directory => System.IO.Path.GetDirectoryName(Path) ?? string.Empty;
    public FileInspection? Inspection { get; private set; }

    public FileEntryViewModel(string path)
    {
        Path = path;
    }

    /// <summary>Reads metadata (file I/O). Safe to call off the UI thread; raises no events.</summary>
    public void InspectData()
    {
        Inspection = StripPipeline.Inspect(Path);
    }

    /// <summary>Raises the change notifications for the last <see cref="InspectData"/>. UI thread only.</summary>
    public void NotifyInspected()
    {
        OnPropertyChanged(nameof(Inspection));
        OnPropertyChanged(nameof(MetadataCount));
        OnPropertyChanged(nameof(FileSizeBytes));
        OnPropertyChanged(nameof(HasError));
        OnPropertyChanged(nameof(Error));
    }

    public int MetadataCount => Inspection?.Entries.Count ?? 0;
    public long FileSizeBytes => Inspection?.FileSizeBytes ?? 0;
    public bool HasError => !string.IsNullOrEmpty(Inspection?.Error);
    public string? Error => Inspection?.Error;

    /// <summary>
    /// The set of chunk keys that the currently selected strip profile would KEEP.
    /// Set by the parent OverlayViewModel whenever the profile changes, since
    /// KeepSet depends on the profile, not on the file's intrinsic state.
    /// </summary>
    public HashSet<string> KeepSet { get; set; } = new(StringComparer.Ordinal);

    public event PropertyChangedEventHandler? PropertyChanged;
    public void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

public sealed class EntryRow
{
    public MetadataEntry Entry { get; }
    public bool WouldKeep { get; }
    public EntryRow(MetadataEntry entry, bool wouldKeep)
    {
        Entry = entry;
        WouldKeep = wouldKeep;
    }
    public string Group => Entry.Group;
    public string Name => Entry.Name;
    public string Value => Entry.Value;
    public string SizeDisplay => Entry.EstimatedSizeBytes is long b ? Formatting.FormatBytes(b) : string.Empty;
    public string Visibility => WouldKeep ? "Would be kept" : "Would be removed";
    public Brush RowBrush => WouldKeep ? Brushes.DimGray : Brushes.Black;
}

public sealed class StripProfileOption
{
    public StripProfile Profile { get; }
    public string Title { get; }
    public string ShortDescription { get; }
    public string LongDescription { get; }
    public StripProfileOption(StripProfile profile)
    {
        Profile = profile;
        var d = StripProfileCatalog.Describe(profile);
        Title = d.Title;
        ShortDescription = d.ShortDescription;
        LongDescription = d.LongDescription;
    }
    public override string ToString() => Title;
}