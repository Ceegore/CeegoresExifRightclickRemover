namespace ExifRemover.App;

/// <summary>
/// Thin pass-through wrapper that re-exports the Engine's <c>Formatting.FormatBytes</c>
/// helper under the <c>ExifRemover.App</c> namespace, preserving the pre-D102 call
/// sites (<c>OverlayViewModel.cs:561</c> and <c>OverlayWindow.xaml.cs:357</c>) without
/// requiring a namespace import change at the call sites. The actual formatting
/// logic lives in <c>ExifRemover.Engine.Formatting</c> (added in D102 / M2.20.40)
/// so the rules can be unit-tested from the Engine test project — the App project
/// is WPF-bound and the xUnit test project can't include its sources.
///
/// D102 (M2.20.40): the pre-fix code duplicated the formatting logic here (a 3-line
/// function with thresholds for B / KB / MB) plus a copy in any future call site.
/// The fix moved the canonical implementation to Engine and replaced this file
/// with a single-line pass-through. The function also gained a GB case (a 1.5 GB
/// file would have rendered as "1500.00 MB" pre-fix) and a negative-value guard.
/// </summary>
internal static class Formatting
{
    public static string FormatBytes(long b) => Engine.Formatting.FormatBytes(b);
}
