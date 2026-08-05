using ExifRemover.Engine;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Direct unit tests for <see cref="Formatting.FormatBytes"/>. D102 (M2.20.40):
/// the pre-fix code had the formatting helper in the App project (WPF-bound,
/// not directly unit-testable from the Engine test project). The fix moved
/// the helper to the Engine project so the formatting rules can be tested
/// directly. The pre-fix function also lacked a GB case (a 1.5 GB file
/// would render as "1500.00 MB" pre-fix) and lacked a negative-value guard.
///
/// The pre-fix test coverage was deferred (per the prior summary:
/// "Formatting.FormatBytes test — WPF-bound, deferred"). The D102 fix
/// removes the WPF-bound constraint and these tests pin the formatter's
/// contract directly.
/// </summary>
public class FormattingTests
{
    [Theory]
    [InlineData(0, "0 B")]            // zero (clamped from -1 by the <= 0 guard)
    [InlineData(1, "1 B")]            // smallest positive
    [InlineData(512, "512 B")]        // mid-range bytes
    [InlineData(1023, "1023 B")]     // largest bytes (just below 1 KB)
    [InlineData(1024, "1.0 KB")]     // exactly 1 KB (boundary)
    [InlineData(1536, "1.5 KB")]     // 1.5 KB
    [InlineData(1024 * 1024 - 1, "1024.0 KB")] // just below 1 MB
    [InlineData(1024 * 1024, "1.00 MB")]      // exactly 1 MB (boundary)
    [InlineData(5 * 1024 * 1024, "5.00 MB")]  // 5 MB
    [InlineData(1023L * 1024 * 1024, "1023.00 MB")] // 1023 MB (just below 1 GB — still MB)
    [InlineData(1024L * 1024 * 1024, "1.00 GB")] // exactly 1 GB (boundary — D102's new case)
    [InlineData(2L * 1024 * 1024 * 1024, "2.00 GB")] // 2 GB
    [InlineData(1500L * 1024 * 1024 * 1024, "1500.00 GB")] // 1.5 TB worth of GB (still GB)
    public void FormatBytes_HandlesAllRanges(long bytes, string expected)
    {
        Assert.Equal(expected, Formatting.FormatBytes(bytes));
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-100)]
    [InlineData(long.MinValue)]
    public void FormatBytes_NegativeValues_ReturnsZeroB(long bytes)
    {
        // D102 (M2.20.40): the pre-fix function had no negative-value guard.
        // A signed-int overflow in an upstream caller (e.g. an
        // int subtraction that goes negative) would render as "-5 B" or
        // similar nonsense. The fix clamps any value <= 0 to "0 B".
        Assert.Equal("0 B", Formatting.FormatBytes(bytes));
    }

    [Fact]
    public void FormatBytes_OnePointFiveGB_UsesGBUnit()
    {
        // D102 (M2.20.40) specific test: the pre-fix function would render
        // a 1.5 GB byte count as "1500.00 MB" (just under the 1500 MB
        // threshold). The fix correctly formats it as "1.46 GB".
        var bytes = (long)(1.46 * 1024 * 1024 * 1024);
        Assert.Equal("1.46 GB", Formatting.FormatBytes(bytes));
    }
}
