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

        App.LaunchWithFiles(paths);
        return 0;
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

    public static void LaunchWithFiles(IReadOnlyList<string> paths)
    {
        InitialPaths = paths;
        var app = new App();
        app.Run();
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
            Shutdown(2);
            return;
        }

        var main = new OverlayWindow(filtered);
        MainWindow = main;
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