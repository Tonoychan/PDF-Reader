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
            ["AccentBrush"] = "#1F6F6B", ["AccentHoverBrush"] = "#165451",
            ["PaperBrush"] = "#F7F5F1", ["ChromeBrush"] = "#FBFAF8", ["ChromeBorderBrush"] = "#E3DFD7",
            ["InkBrush"] = "#1D1C1A", ["MutedInkBrush"] = "#66625A", ["HoverBrush"] = "#EEEBE5",
            ["ActiveParagraphBrush"] = "#EFE8D6", ["ReadingWordBrush"] = "#FFCC33", ["ReadingWordTextBrush"] = "#1E1C18",
            ["ReadingSentenceBrush"] = "#FFDA6B", ["CursorWordBrush"] = "#CFE6E3", ["SelectedBrush"] = "#DCEDEB",
            ["SelectedTextBrush"] = "#1F6F6B", ["PageBackdropBrush"] = "#EFEDE8", ["OverlayBrush"] = "#B3F7F5F1",
        },
        [Sepia] = new()
        {
            ["AccentBrush"] = "#8B5A2B", ["AccentHoverBrush"] = "#6F4721",
            ["PaperBrush"] = "#F4ECD8", ["ChromeBrush"] = "#F9F2E3", ["ChromeBorderBrush"] = "#E2D5B9",
            ["InkBrush"] = "#3E3020", ["MutedInkBrush"] = "#74634D", ["HoverBrush"] = "#EDE2C9",
            ["ActiveParagraphBrush"] = "#EAD9B2", ["ReadingWordBrush"] = "#F5BE3A", ["ReadingWordTextBrush"] = "#2A2014",
            ["ReadingSentenceBrush"] = "#F7D27A", ["CursorWordBrush"] = "#E4D3AE", ["SelectedBrush"] = "#EBDDBF",
            ["SelectedTextBrush"] = "#6F4721", ["PageBackdropBrush"] = "#E8DDC4", ["OverlayBrush"] = "#B3F4ECD8",
        },
        [Dark] = new()
        {
            ["AccentBrush"] = "#2E8C86", ["AccentHoverBrush"] = "#36A39C",
            ["PaperBrush"] = "#161616", ["ChromeBrush"] = "#1B1B1B", ["ChromeBorderBrush"] = "#2E2E2E",
            ["InkBrush"] = "#ECE9E3", ["MutedInkBrush"] = "#A39E95", ["HoverBrush"] = "#262626",
            ["ActiveParagraphBrush"] = "#24221D", ["ReadingWordBrush"] = "#FFCC33", ["ReadingWordTextBrush"] = "#1E1C18",
            ["ReadingSentenceBrush"] = "#E3C062", ["CursorWordBrush"] = "#1F4140", ["SelectedBrush"] = "#1C3634",
            ["SelectedTextBrush"] = "#6CC9C1", ["PageBackdropBrush"] = "#121212", ["OverlayBrush"] = "#B3161616",
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
