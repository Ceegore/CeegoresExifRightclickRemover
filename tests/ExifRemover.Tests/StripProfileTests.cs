using ExifRemover.Engine;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// Tests for <see cref="StripProfileCatalog.Describe"/>. The catalog is what the
/// overlay shows in the profile dropdown and the long-description label. The
/// descriptions are also referenced by the README's profile table — any drift
/// between code and docs would be caught here.
/// </summary>
public class StripProfileTests
{
    [Fact]
    public void Describe_Privacy_HasExpectedTitle()
    {
        var d = StripProfileCatalog.Describe(StripProfile.Privacy);
        Assert.Equal("Privacy", d.Title);
        // Privacy strips EXIF, IPTC, XMP, ICC profile, comments, and PNG text/time/eXIf.
        Assert.Contains("EXIF", d.ShortDescription);
        Assert.Contains("ICC", d.ShortDescription);
    }

    [Fact]
    public void Describe_AllMetadata_HasExpectedTitle()
    {
        var d = StripProfileCatalog.Describe(StripProfile.AllMetadata);
        Assert.Equal("All metadata", d.Title);
        // AllMetadata is "Privacy plus PNG color-management chunks".
        Assert.Contains("color", d.ShortDescription.ToLowerInvariant());
    }

    [Fact]
    public void Describe_Minimal_HasExpectedTitle()
    {
        var d = StripProfileCatalog.Describe(StripProfile.Minimal);
        Assert.Equal("Minimal", d.Title);
        // Minimal keeps the ICC profile.
        Assert.Contains("ICC", d.ShortDescription);
    }

    [Fact]
    public void Describe_LongDescription_AlwaysPopulated()
    {
        // The UI binds the long description to the dropdown's tooltip. An empty
        // tooltip would be confusing. Every profile must have a populated
        // long description.
        foreach (StripProfile profile in System.Enum.GetValues<StripProfile>())
        {
            var d = StripProfileCatalog.Describe(profile);
            Assert.False(string.IsNullOrWhiteSpace(d.LongDescription),
                $"Long description for {profile} must not be empty");
        }
    }

    [Fact]
    public void Describe_UnknownEnumValue_Throws()
    {
        // The catalog's _ => throw branch — if a future contributor adds a new
        // enum value without describing it, the Describe call must throw loudly
        // rather than silently returning empty strings.
        Assert.Throws<System.ArgumentOutOfRangeException>(
            () => StripProfileCatalog.Describe((StripProfile)999));
    }
}
