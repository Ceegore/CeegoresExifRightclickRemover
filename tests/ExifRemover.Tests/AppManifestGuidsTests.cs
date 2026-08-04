using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace ExifRemover.Tests;

/// <summary>
/// "No fabrication" guard for <c>src/ExifRemover.App/app.manifest</c>.
///
/// The M2.20.7 D48 round added a Windows 11 <c>supportedOS</c> GUID
/// ({e1b086e2-5834-4d6b-a0c5-321d5705261c}) that is NOT a real Microsoft-published
/// value. Microsoft's official position — documented in
/// https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests
/// and in ntdll.dll's SbSupportedOsList — is that Windows 10/11 share the same
/// GUID ({8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}), and that no separate Windows 11
/// GUID has been published. The fake GUID survived 10 audit rounds before the
/// M2.20.17 round caught it (D67).
///
/// This test pins the supportedOS list to the 5 official Microsoft GUIDs so any
/// future "let me add a Win 12 / Server 2025 GUID" suggestion fails loudly with
/// a clear "this GUID is not in the Microsoft-published list" message instead of
/// silently going into the manifest.
/// </summary>
public class AppManifestGuidsTests
{
    /// <summary>
    /// The 5 supportedOS GUIDs that Microsoft actually publishes (per the official
    /// docs page and the ntdll.dll SbSupportedOsList). If a new Windows version
    /// publishes a new GUID, add it here AND cite the Microsoft source — otherwise
    /// the test fails, which is the desired "no fabrication" behavior.
    /// </summary>
    private static readonly HashSet<string> OfficialMicrosoftGuids = new(System.StringComparer.OrdinalIgnoreCase)
    {
        "{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}", // Windows 10 + Windows 11 + Server 2016/2019/2022
        "{1f676c76-80e1-4239-95bb-83d0f6d0da78}", // Windows 8.1 + Server 2012 R2
        "{4a2f28e3-53b9-4441-ba9c-d69d4a4a6e38}", // Windows 8 + Server 2012
        "{35138b9a-5d96-4fbd-8e2d-a2440225f93a}", // Windows 7 + Server 2008 R2
        "{e2011457-1546-43c5-a5fe-008deee3d3f0}", // Windows Vista + Server 2008
    };

    [Fact]
    public void AppManifest_AllSupportedOsGuids_AreMicrosoftPublished()
    {
        var manifestPath = LocateManifest();
        Assert.True(File.Exists(manifestPath),
            $"Cannot find app.manifest at {manifestPath}. The test lives in tests/ExifRemover.Tests/ and expects the manifest at src/ExifRemover.App/app.manifest relative to the repo root.");

        var doc = XDocument.Load(manifestPath);
        var asm = doc.Root ?? throw new InvalidDataException("app.manifest has no root element.");
        var compatNs = asm.GetNamespaceOfPrefix("asm") ?? asm.GetDefaultNamespace();
        // The compatibility element lives in the urn:schemas-microsoft-com:compatibility.v1 namespace.
        XNamespace compat = "urn:schemas-microsoft-com:compatibility.v1";
        var compatibility = asm.Element(compat + "compatibility");
        Assert.NotNull(compatibility);

        var application = compatibility!.Element(compat + "application");
        Assert.NotNull(application);

        var supportedOsIds = application!
            .Elements(compat + "supportedOS")
            .Select(e => (string?)e.Attribute("Id") ?? string.Empty)
            .ToList();

        // Sanity: the manifest must declare at least one supportedOS entry.
        Assert.NotEmpty(supportedOsIds);

        foreach (var id in supportedOsIds)
        {
            Assert.True(
                OfficialMicrosoftGuids.Contains(id),
                $"app.manifest declares supportedOS Id={id} which is NOT in the Microsoft-published list. " +
                $"Per https://learn.microsoft.com/en-us/windows/win32/sbscs/application-manifests and " +
                $"ntdll.dll's SbSupportedOsList, only these GUIDs are Microsoft-published: " +
                $"{string.Join(", ", OfficialMicrosoftGuids)}. Windows 10 and Windows 11 share " +
                $"{{8e0f7a12-bfb3-4fe8-b9a5-48fd50a15a9a}} — there is no separate Windows 11 GUID. " +
                $"If a new Windows version genuinely publishes a new GUID, add it to OfficialMicrosoftGuids " +
                $"AND cite the Microsoft source (the docs page or a Microsoft blog post).");
        }
    }

    [Fact]
    public void AppManifest_DoesNotDeclareFabricatedWin11Guid()
    {
        // D67 regression: the M2.20.7 D48 round added
        // {e1b086e2-5834-4d6b-a0c5-321d5705261c} as a "Windows 11 supportedOS GUID".
        // It is NOT a real Microsoft GUID. This test pins the removal of that
        // specific fabricated entry with a named regression message.
        var manifestPath = LocateManifest();
        var doc = XDocument.Load(manifestPath);
        var compat = "urn:schemas-microsoft-com:compatibility.v1";
        XNamespace compatNs = compat;
        var fakeGuid = "{e1b086e2-5834-4d6b-a0c5-321d5705261c}";

        var found = doc.Descendants(compatNs + "supportedOS")
            .Any(e => string.Equals((string?)e.Attribute("Id"), fakeGuid, System.StringComparison.OrdinalIgnoreCase));

        Assert.False(found,
            "app.manifest still contains the fabricated Win 11 GUID " + fakeGuid + ". " +
            "This is not a Microsoft-published value. The Win 10 GUID already covers Win 11.");
    }

    private static string LocateManifest()
    {
        // The test DLL sits at <repo>/tests/ExifRemover.Tests/bin/Release/net8.0/.
        // The manifest sits at <repo>/src/ExifRemover.App/app.manifest. Walk up to
        // the repo root and join. This mirrors VerifierProcessTests.LocateVerifier.
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var candidate = Path.Combine(dir, "src", "ExifRemover.App", "app.manifest");
            if (File.Exists(candidate)) return candidate;
            var parent = Directory.GetParent(dir);
            if (parent is null) return Path.Combine(dir, "src", "ExifRemover.App", "app.manifest");
            dir = parent.FullName;
        }
        return Path.Combine(AppContext.BaseDirectory, "src", "ExifRemover.App", "app.manifest");
    }
}
