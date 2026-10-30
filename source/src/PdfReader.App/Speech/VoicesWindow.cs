using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PdfReader.Core;

namespace PdfReader.App.Speech;

/// <summary>
/// Browse, download and remove natural (Piper) voices. The first download also installs the
/// voice engine. Windows voices are added in Windows Settings (button at the bottom).
/// </summary>
public sealed class VoicesWindow : Window
{
    private readonly ComboBox _language = new() { Width = 260, Margin = new Thickness(0, 0, 10, 0), Padding = new Thickness(8, 5, 8, 5) };
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _rows = new();
    private List<PiperVoiceInfo> _catalog = new();
    private CancellationTokenSource? _download;
    private bool _busy;

    /// <summary>Voice the user chose with "Use" or just downloaded (key like "piper:en_US-lessac-medium").</summary>
    public string? ChosenVoiceKey { get; private set; }

    /// <summary>True if anything was installed or removed (the voice list needs refreshing).</summary>
    public bool Changed { get; private set; }

    public VoicesWindow()
    {
        Title = "Voices";
        Width = 760;
        Height = 680;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Res<Brush>("PaperBrush");
        FontFamily = Res<FontFamily>("UiFont");
        Foreground = Res<Brush>("InkBrush");

        var root = new DockPanel { Margin = new Thickness(26, 22, 26, 18) };

        // Header
        var header = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        header.Children.Add(new TextBlock { Text = "Natural voices", FontSize = 22, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock
        {
            Text = "Free voices that run on this PC (Piper). They sound much more natural than the Windows voices. " +
                   "While they read, the whole sentence is highlighted. Each voice is a one-time download of about 20–120 MB.",
            TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Foreground = Res<Brush>("MutedInkBrush"), Margin = new Thickness(0, 6, 0, 0),
        });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        // Filter row
        var filter = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        filter.Children.Add(_language);
        filter.Children.Add(_status);
        DockPanel.SetDock(filter, Dock.Top);
        root.Children.Add(filter);
        _language.SelectionChanged += (_, _) => BuildRows();

        // Footer
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        var close = new Button { Content = "Close", Style = Res<Style>("AccentButton"), Padding = new Thickness(20, 8, 20, 8), IsCancel = true };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Right);
        footer.Children.Add(close);
        var winVoices = new Button { Content = "Add Windows voices…", Style = Res<Style>("ToolButton"), HorizontalAlignment = HorizontalAlignment.Left };
        winVoices.ToolTip = "Opens Windows Settings › Time & language › Speech";
        winVoices.Click += (_, _) => { try { Process.Start(new ProcessStartInfo("ms-settings:speech") { UseShellExecute = true }); } catch { } };
        footer.Children.Add(winVoices);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        // Voice list
        var scroll = new ScrollViewer
        {
            Content = _rows,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Res<Brush>("ChromeBrush"),
            BorderBrush = Res<Brush>("ChromeBorderBrush"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6),
        };
        root.Children.Add(scroll);

        Content = root;
        Loaded += async (_, _) => await LoadCatalogAsync(refresh: false);
        Closing += (_, e) =>
        {
            if (_busy && MessageBox.Show(this, "A voice is still downloading. Stop the download and close?", "Voices",
                    MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) e.Cancel = true;
            else _download?.Cancel();
        };
    }

    private T Res<T>(string key) where T : class => (T)Application.Current.FindResource(key);

    private async Task LoadCatalogAsync(bool refresh)
    {
        _status.Text = "Loading the voice list…";
        try
        {
            _catalog = await PiperInstall.LoadCatalogAsync(refresh, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _status.Text = "Couldn't load the voice list. Check your internet connection. (" + ex.Message + ")";
            _catalog = new();
        }

        var languages = _catalog.Select(v => v.LanguageName).Distinct().OrderBy(x => x).ToList();
        _language.ItemsSource = new[] { "English voices", "All languages" }.Concat(languages.Where(l => !l.StartsWith("English"))).ToList();
        _language.SelectedIndex = 0;
        BuildRows();
    }

    private void BuildRows()
    {
        _rows.Children.Clear();
        string choice = _language.SelectedItem as string ?? "English voices";
        var voices = choice switch
        {
            "English voices" => _catalog.Where(v => v.IsEnglish),
            "All languages" => _catalog,
            _ => _catalog.Where(v => v.LanguageName == choice),
        };
        // Installed first, then recommended, then the rest.
        var list = voices
            .OrderByDescending(v => PiperInstall.IsInstalled(v.Key))
            .ThenBy(v => Array.IndexOf(PiperCatalog.Recommended, v.Key) is var i && i >= 0 ? i : int.MaxValue)
            .ThenBy(v => v.LanguageName).ThenBy(v => v.Name).ThenBy(v => PiperCatalog.QualityRank(v.Quality))
            .ToList();
        foreach (var v in list) _rows.Children.Add(BuildRow(v));

        int installed = _catalog.Count(v => PiperInstall.IsInstalled(v.Key));
        if (_catalog.Count > 0)
            _status.Text = $"{list.Count} voices · {installed} installed" +
                           (PiperInstall.EngineInstalled ? "" : " · the first download also gets the voice engine (about 21 MB)");
    }

    private FrameworkElement BuildRow(PiperVoiceInfo v)
    {
        bool installed = PiperInstall.IsInstalled(v.Key);
        bool recommended = PiperCatalog.Recommended.Contains(v.Key);

        var grid = new Grid { Margin = new Thickness(10, 8, 10, 8) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var title = new TextBlock { FontSize = 14.5, FontWeight = FontWeights.SemiBold };
        title.Inlines.Add(v.DisplayName);
        if (recommended) title.Inlines.Add(new System.Windows.Documents.Run("  ★ recommended") { FontSize = 11.5, FontWeight = FontWeights.Normal, Foreground = Res<Brush>("AccentBrush") });
        if (installed) title.Inlines.Add(new System.Windows.Documents.Run("  ✓ installed") { FontSize = 11.5, FontWeight = FontWeights.Normal, Foreground = Brushes.SeaGreen });
        text.Children.Add(title);
        string speakers = v.Speakers > 1 ? $" · {v.Speakers} speakers" : "";
        text.Children.Add(new TextBlock
        {
            Text = $"{v.LanguageName} · {QualityLabel(v.Quality)} · {v.SizeLabel}{speakers}",
            FontSize = 12.5, Foreground = Res<Brush>("MutedInkBrush"), Margin = new Thickness(0, 2, 0, 0),
        });
        var progress = new ProgressBar { Height = 4, Margin = new Thickness(0, 6, 0, 0), Maximum = 1, Visibility = Visibility.Collapsed, Foreground = Res<Brush>("AccentBrush") };
        var progressText = new TextBlock { FontSize = 12, Foreground = Res<Brush>("MutedInkBrush"), Visibility = Visibility.Collapsed };
        text.Children.Add(progress);
        text.Children.Add(progressText);
        grid.Children.Add(text);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(buttons, 1);
        if (installed)
        {
            var use = new Button { Content = "Use this voice", Style = Res<Style>("AccentButton"), Padding = new Thickness(14, 6, 14, 6) };
            use.Click += (_, _) => { ChosenVoiceKey = "piper:" + v.Key; Close(); };
            var remove = new Button { Content = "Remove", Style = Res<Style>("ToolButton"), Margin = new Thickness(6, 0, 0, 0) };
            remove.Click += (_, _) =>
            {
                if (MessageBox.Show(this, $"Remove the voice {v.DisplayName}? You can download it again any time.", "Voices",
                        MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
                PiperInstall.RemoveVoice(v.Key);
                Changed = true;
                BuildRows();
            };
            buttons.Children.Add(use);
            buttons.Children.Add(remove);
        }
        else
        {
            var download = new Button { Content = "Download", Style = Res<Style>("ToolButton"), Padding = new Thickness(14, 6, 14, 6), IsEnabled = !_busy };
            download.Click += async (_, _) => await DownloadAsync(v, download, progress, progressText);
            buttons.Children.Add(download);
        }
        grid.Children.Add(buttons);

        return new Border
        {
            Child = grid,
            BorderBrush = Res<Brush>("ChromeBorderBrush"),
            BorderThickness = new Thickness(0, 0, 0, 1),
        };
    }

    private static string QualityLabel(string q) => q switch
    {
        "high" => "high quality",
        "medium" => "medium quality",
        "low" => "low quality (small)",
        "x_low" => "lowest quality (tiny)",
        _ => q,
    };

    private async Task DownloadAsync(PiperVoiceInfo v, Button button, ProgressBar bar, TextBlock label)
    {
        if (_busy) return;
        _busy = true;
        foreach (var b in FindButtons(_rows)) b.IsEnabled = false;
        button.Content = "Downloading…";
        bar.Visibility = label.Visibility = Visibility.Visible;
        _download = new CancellationTokenSource();
        var progress = new Progress<(string Step, double Fraction)>(p =>
        {
            bar.Value = p.Fraction;
            label.Text = p.Fraction > 0 ? $"{p.Step}… {p.Fraction * 100:0}%" : $"{p.Step}…";
        });
        try
        {
            await PiperInstall.InstallVoiceAsync(v, progress, _download.Token);
            Changed = true;
            ChosenVoiceKey = "piper:" + v.Key;
        }
        catch (OperationCanceledException)
        {
            PiperInstall.RemoveVoice(v.Key);
        }
        catch (Exception ex)
        {
            PiperInstall.RemoveVoice(v.Key);
            MessageBox.Show(this, $"Couldn't download {v.DisplayName}.\n\n{ex.Message}", "Voices", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _busy = false;
            _download = null;
            BuildRows();
        }
    }

    private static IEnumerable<Button> FindButtons(DependencyObject root)
    {
        int n = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < n; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is Button b) yield return b;
            foreach (var nested in FindButtons(child)) yield return nested;
        }
    }
}

/// <summary>Edits the pronunciation word list ("word = say it like this", one per line).</summary>
public sealed class WordListWindow : Window
{
    private readonly TextBox _box;

    public Dictionary<string, string>? Result { get; private set; }

    public WordListWindow(IReadOnlyDictionary<string, string> current)
    {
        Title = "Pronunciation word list";
        Width = 560;
        Height = 520;
        MinWidth = 420;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.FindResource("PaperBrush");
        FontFamily = (FontFamily)Application.Current.FindResource("UiFont");

        var root = new DockPanel { Margin = new Thickness(22, 18, 22, 16) };
        var help = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap, FontSize = 13.5, Margin = new Thickness(0, 0, 0, 10),
            Text = "One word per line, then = and how it should be said. Matching ignores upper/lower case.\n" +
                   "Examples:   LTS = long term support     URP = U R P     Tonoy = Tonoy\n" +
                   "Leave the right side empty to skip a word.",
        };
        DockPanel.SetDock(help, Dock.Top);
        root.Children.Add(help);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var save = new Button { Content = "Save", Style = (Style)Application.Current.FindResource("AccentButton"), Padding = new Thickness(20, 8, 20, 8), IsDefault = false };
        var cancel = new Button { Content = "Cancel", Style = (Style)Application.Current.FindResource("ToolButton"), Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        save.Click += (_, _) => { Result = WordListText.Parse(_box!.Text); DialogResult = true; };
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);

        _box = new TextBox
        {
            Text = WordListText.Format(current),
            AcceptsReturn = true,
            AcceptsTab = false,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 14,
            Padding = new Thickness(8),
        };
        root.Children.Add(_box);
        Content = root;
        Loaded += (_, _) => { _box.Focus(); _box.CaretIndex = _box.Text.Length; };
    }
}
