using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Resources;
using ExifRemover.Engine;

namespace ExifRemover.App;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        AttachToParentConsole();

        var paths = SplitArgs(args);

        if (paths.Count == 0)
        {
            Console.Error.WriteLine("ExifRemover: no input files.");
            Console.Error.WriteLine("Usage: ExifRemover.exe <image> [<image> ...]");
            return 2;
        }

        return App.LaunchWithFiles(paths);
    }

    internal static List<string> SplitArgs(string[] args)
    {
        var result = new List<string>(args.Length);
        foreach (var a in args)
        {
            if (!string.IsNullOrWhiteSpace(a))
            {
                result.Add(a);
            }
        }
        return result;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AllocConsole();

    private const int ATTACH_PARENT_PROCESS = -1;

    private static void AttachToParentConsole()
    {
        try
        {
            if (!AttachConsole(ATTACH_PARENT_PROCESS))
            {
                AllocConsole();
            }
        }
        catch
        {
        }
    }
}

/// <summary>
/// Plain (non-XAML) Application subclass. Loaded as the entry point by Program.Main
/// so we can avoid the WPF source generator creating an auto-Main from App.xaml.
/// The theme dictionary is loaded from Resources/Theme.xaml as a Pack URI resource.
/// </summary>
public class App : Application
{
    public static IReadOnlyList<string> InitialPaths { get; private set; } = Array.Empty<string>();
    public static StripProfile OverlayProfile { get; set; } = StripProfile.Privacy;

    public App()
    {
        // Load the theme resources from the assembly's pack URI.
        try
        {
            var themeUri = new Uri("/ExifRemover;component/Resources/Theme.xaml", UriKind.Relative);
            var rd = new ResourceDictionary { Source = themeUri };
            Resources.MergedDictionaries.Add(rd);
        }
        catch
        {
            // If resources can't load, the app still runs with default styling.
        }

        this.Startup += App_Startup;
    }

    /// <summary>
    /// Boots the WPF app with the given input paths. Returns the WPF shutdown code, which
    /// is 0 on a successful run and 2 when no input was usable. The previous version
    /// returned 0 unconditionally, which made the no-input case indistinguishable from
    /// success for callers (CI scripts, .cmd wrappers, the install/uninstall harness).
    /// </summary>
    public static int LaunchWithFiles(IReadOnlyList<string> paths)
    {
        InitialPaths = paths;
        var app = new App();
        return app.Run();
    }

    private void App_Startup(object sender, StartupEventArgs e)
    {
        var paths = (InitialPaths.Count > 0 ? InitialPaths : e.Args).ToList();
        if (paths.Count == 0)
        {
            Shutdown(2);
            return;
        }

        var filtered = FilterSupported(paths);
        if (filtered.Count == 0)
        {
            Console.Error.WriteLine(
                "ExifRemover: none of the selected files are supported. Only .jpg, .jpeg, and .png are.");
            Shutdown(2);
            return;
        }

        // D1: build the overlay window FIRST, then set MainWindow, THEN surface the
        // dropped-files notice. The previous order (notice first, window second) made
        // `MainWindow is OverlayWindow mw` always false — MainWindow was still null at
        // that point — so the overlay never showed the dropped-files notice. The
        // console.Error line still fired, but the UI message was dead code. The
        // contract that "unsupported files in a multi-select are visible" is now
        // wired to actually show on the overlay.
        var main = new OverlayWindow(filtered);
        MainWindow = main;

        if (filtered.Count < paths.Count)
        {
            var dropped = new List<string>();
            var kept = new HashSet<string>(filtered, StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths)
            {
                if (!kept.Contains(p))
                {
                    dropped.Add(p);
                }
            }
            var msg = $"ExifRemover: ignored {dropped.Count} unsupported file(s): " +
                      string.Join(", ", dropped.ConvertAll(Path.GetFileName));
            Console.Error.WriteLine(msg);
            main.SetNonFatalNotice(msg);
        }

        main.Show();
    }

    private static List<string> FilterSupported(IEnumerable<string> paths)
    {
        var list = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                var ext = Path.GetExtension(p);
                if (string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase))
                {
                    list.Add(Path.GetFullPath(p));
                }
            }
            catch
            {
            }
        }
        return list;
    }
}