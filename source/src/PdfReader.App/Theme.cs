using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using PdfReader.Core;

namespace PdfReader.App;

/// <summary>
/// Light, Sepia and Dark colours. Every colour in the app comes from a named brush in App.xaml and is
/// used as a DynamicResource, so swapping the brushes here recolours the whole window at once.
/// </summary>
public static class Theme
{
    public const string Light = "Light", Sepia = "Sepia", Dark = "Dark";

    private static readonly Dictionary<string, Dictionary<string, string>> Palettes = new()
    {
        [Light] = new()
        {
            ["AccentBrush"] = "#2B5BD7", ["AccentHoverBrush"] = "#2249B0",
            ["PaperBrush"] = "#FBF8F1", ["ChromeBrush"] = "#FFFFFF", ["ChromeBorderBrush"] = "#E7E1D4",
            ["InkBrush"] = "#2A2824", ["MutedInkBrush"] = "#5F5A50", ["HoverBrush"] = "#F1ECE1",
            ["ActiveParagraphBrush"] = "#F1E7CF", ["ReadingWordBrush"] = "#FFCC33", ["ReadingWordTextBrush"] = "#1E1C18",
            ["ReadingSentenceBrush"] = "#FFDA6B", ["CursorWordBrush"] = "#DCE5FB", ["SelectedBrush"] = "#E4EAFB",
            ["PageBackdropBrush"] = "#ECE8DF", ["OverlayBrush"] = "#B3FBF8F1",
        },
        [Sepia] = new()
        {
            ["AccentBrush"] = "#9C5A2B", ["AccentHoverBrush"] = "#7F4720",
            ["PaperBrush"] = "#F4ECD8", ["ChromeBrush"] = "#FAF4E5", ["ChromeBorderBrush"] = "#E0D2B4",
            ["InkBrush"] = "#3E3020", ["MutedInkBrush"] = "#76654F", ["HoverBrush"] = "#ECE0C6",
            ["ActiveParagraphBrush"] = "#EAD9B2", ["ReadingWordBrush"] = "#F5BE3A", ["ReadingWordTextBrush"] = "#2A2014",
            ["ReadingSentenceBrush"] = "#F7D27A", ["CursorWordBrush"] = "#DCD6EC", ["SelectedBrush"] = "#EBDCC0",
            ["PageBackdropBrush"] = "#E3D7BD", ["OverlayBrush"] = "#B3F4ECD8",
        },
        [Dark] = new()
        {
            ["AccentBrush"] = "#4C7BE6", ["AccentHoverBrush"] = "#6690F0",
            ["PaperBrush"] = "#1C1C1F", ["ChromeBrush"] = "#26262A", ["ChromeBorderBrush"] = "#3A3A40",
            ["InkBrush"] = "#E4E1DA", ["MutedInkBrush"] = "#A29D93", ["HoverBrush"] = "#323238",
            ["ActiveParagraphBrush"] = "#2D2B25", ["ReadingWordBrush"] = "#FFCC33", ["ReadingWordTextBrush"] = "#1E1C18",
            ["ReadingSentenceBrush"] = "#E3C062", ["CursorWordBrush"] = "#34466E", ["SelectedBrush"] = "#2F3B58",
            ["PageBackdropBrush"] = "#131315", ["OverlayBrush"] = "#B31C1C1F",
        },
    };

    public static string Current { get; private set; } = Light;
    public static bool IsDark => Current == Dark;

    public static string Normalize(string? name) => name is Sepia or Dark ? name : Light;

    /// <summary>How pages should be recoloured for <paramref name="theme"/> when "match the theme" is on.</summary>
    public static PageTint TintFor(string theme, bool matchPages) =>
        !matchPages ? PageTint.None : theme switch { Sepia => PageTint.Sepia, Dark => PageTint.Dark, _ => PageTint.None };

    public static void Apply(string name, bool matchPages)
    {
        name = Normalize(name);
        var resources = Application.Current.Resources;
        foreach (var (key, hex) in Palettes[name])
            resources[key] = Frozen((Color)ColorConverter.ConvertFromString(hex));

        // The blank page shown before a page is drawn matches what the drawn page will look like.
        var blank = TintFor(name, matchPages) switch
        {
            PageTint.Sepia => Color.FromRgb(PageTinter.SepiaPaper.R, PageTinter.SepiaPaper.G, PageTinter.SepiaPaper.B),
            PageTint.Dark => Color.FromRgb(PageTinter.DarkPaper, PageTinter.DarkPaper, PageTinter.DarkPaper),
            _ => Colors.White,
        };
        resources["PageBlankBrush"] = Frozen(blank);
        Current = name;

        foreach (Window w in Application.Current.Windows) ApplyTitleBar(w);
    }

    private static SolidColorBrush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    // ---- window title bar ----------------------------------------------------------------------

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Dark title bar in the Dark theme (Windows 10 2004+ / Windows 11; ignored elsewhere).</summary>
    public static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int on = IsDark ? 1 : 0;
        try
        {
            if (DwmSetWindowAttribute(hwnd, 20, ref on, sizeof(int)) != 0)
                DwmSetWindowAttribute(hwnd, 19, ref on, sizeof(int)); // older Windows 10 builds
        }
        catch { }
    }
}
