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
    private readonly List<string> _allPaths;
    private readonly ObservableCollection<FileEntryViewModel> _files = new();
    private readonly Dictionary<string, FileEntryViewModel> _byPath = new(StringComparer.OrdinalIgnoreCase);

    public ObservableCollection<FileEntryViewModel> Files => _files;
    public ICollectionView FilesView => CollectionViewSource.GetDefaultView(_files);
    public ObservableCollection<StripProfileOption> Profiles { get; } = new();
    public ObservableCollection<object> AllEntries { get; } = new();

    public ICollectionView EntriesView { get; }

    public OverlayViewModel(IReadOnlyList<string> paths, Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _allPaths = paths.ToList();
        foreach (var p in _allPaths)
        {
            if (!File.Exists(p))
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
            _byPath[p] = vm;
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
        get => _statusText;
        set
        {
            if (_statusText == value) return;
            _statusText = value;
            OnPropertyChanged();
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

    private static string GetChunkKey(MetadataEntry entry)
    {
        if (entry.Group.StartsWith("EXIF", StringComparison.Ordinal)) return "EXIF";
        if (entry.Group == MetadataGroups.Iptc) return "IPTC";
        if (entry.Group == MetadataGroups.Xmp) return "XMP";
        if (entry.Group == MetadataGroups.Icc) return "ICC";
        if (entry.Group == MetadataGroups.JpegComment) return "COM";
        if (entry.Group == MetadataGroups.PngText) return "PNGTEXT";
        if (entry.Group == MetadataGroups.PngTime) return "PNGTIME";
        if (entry.Group == MetadataGroups.PngExif) return "PNGEXIF";
        if (entry.Group == MetadataGroups.PngIccp) return "PNGICCP";
        if (entry.Group == MetadataGroups.PngHist) return "PNGHIST";
        if (entry.Group == MetadataGroups.PngSrgb) return "PNGSRGB";
        if (entry.Group == MetadataGroups.PngChrm) return "PNGCHRM";
        if (entry.Group == MetadataGroups.PngGama) return "PNGGAMA";
        if (entry.Group == MetadataGroups.PngPhys) return "PNGPHYS";
        if (entry.Group == MetadataGroups.PngBkgd) return "PNGBKGD";
        if (entry.Group == MetadataGroups.PngSbit) return "PNGSBIT";
        if (entry.Group == MetadataGroups.PngTrns) return "PNGTRNS";
        // D2: explicit case for PngUnknown (otherwise the function falls through to
        // `return entry.Group` which yields "PNG Unknown" with a space, never matching
        // the keep-set key, so the grid would show "Would be removed" for a chunk the
        // stripper keeps).
        if (entry.Group == MetadataGroups.PngUnknown) return "PNGUNKNOWN";
        return entry.Group;
    }

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
    public string SizeDisplay => Entry.EstimatedSizeBytes is long b ? FormatBytes(b) : string.Empty;
    public string Visibility => WouldKeep ? "Would be kept" : "Would be removed";
    public Brush RowBrush => WouldKeep ? Brushes.DimGray : Brushes.Black;
    private static string FormatBytes(long b)
    {
        if (b < 1024) return $"{b} B";
        if (b < 1024 * 1024) return $"{b / 1024.0:0.0} KB";
        return $"{b / 1024.0 / 1024.0:0.00} MB";
    }
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