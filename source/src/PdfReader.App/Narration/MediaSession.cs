using System.IO;
using System.Windows.Threading;
using global::Windows.Media;
using global::Windows.Storage;
using global::Windows.Storage.Streams;

namespace PdfReader.App.Narration;

/// <summary>
/// Windows media controls for the reader: keyboard media keys, headset buttons, and the media panel in
/// Quick Settings / the volume pop-up (book title, chapter, cover, play/pause, next/previous paragraph).
/// </summary>
public sealed class MediaSession
{
    private readonly SystemMediaTransportControls _smtc;
    private readonly Dispatcher _dispatcher;
    private string? _title, _subtitle, _cover;
    private int _coverVersion;

    /// <summary>A media button was pressed (raised on the UI thread).</summary>
    public event Action<SystemMediaTransportControlsButton>? ButtonPressed;

    private MediaSession(SystemMediaTransportControls smtc, Dispatcher dispatcher)
    {
        _smtc = smtc;
        _dispatcher = dispatcher;
        _smtc.IsPlayEnabled = true;
        _smtc.IsPauseEnabled = true;
        _smtc.IsStopEnabled = true;
        _smtc.IsNextEnabled = true;
        _smtc.IsPreviousEnabled = true;
        _smtc.ButtonPressed += (_, e) =>
        {
            var button = e.Button;
            _dispatcher.InvokeAsync(() => ButtonPressed?.Invoke(button));
        };
        _smtc.IsEnabled = false;
    }

    /// <summary>Connects to the media controls for <paramref name="hwnd"/>; null if Windows doesn't offer them.</summary>
    public static MediaSession? TryCreate(IntPtr hwnd, Dispatcher dispatcher)
    {
        try { return new MediaSession(SystemMediaTransportControlsInterop.GetForWindow(hwnd), dispatcher); }
        catch { return null; }
    }

    /// <summary>Shows or hides the session and its play/pause state.</summary>
    public void SetState(bool hasBook, NarrationState state)
    {
        try
        {
            if (!hasBook)
            {
                _smtc.PlaybackStatus = MediaPlaybackStatus.Closed;
                _smtc.IsEnabled = false;
                return;
            }
            _smtc.IsEnabled = true;
            _smtc.PlaybackStatus = state switch
            {
                NarrationState.Playing => MediaPlaybackStatus.Playing,
                NarrationState.Paused => MediaPlaybackStatus.Paused,
                _ => MediaPlaybackStatus.Stopped,
            };
        }
        catch { }
    }

    /// <summary>Book title, a second line (chapter or page) and the cover picture file.</summary>
    public void SetInfo(string title, string subtitle, string? coverFile)
    {
        if (title == _title && subtitle == _subtitle && coverFile == _cover) return;
        bool coverChanged = coverFile != _cover;
        _title = title;
        _subtitle = subtitle;
        _cover = coverFile;
        try
        {
            var display = _smtc.DisplayUpdater;
            display.Type = MediaPlaybackType.Music;
            display.MusicProperties.Title = title;
            display.MusicProperties.Artist = subtitle;
            display.MusicProperties.AlbumTitle = "PDF Reader";
            if (coverChanged) display.Thumbnail = null;
            display.Update();
        }
        catch { }
        if (coverChanged && coverFile is not null) _ = LoadCoverAsync(coverFile, ++_coverVersion);
    }

    private async Task LoadCoverAsync(string file, int version)
    {
        try
        {
            if (!File.Exists(file)) return;
            var storage = await StorageFile.GetFileFromPathAsync(file);
            if (version != _coverVersion) return;
            var display = _smtc.DisplayUpdater;
            display.Thumbnail = RandomAccessStreamReference.CreateFromFile(storage);
            display.Update();
        }
        catch { }
    }

    public void Clear()
    {
        _title = _subtitle = _cover = null;
        _coverVersion++;
        try
        {
            _smtc.DisplayUpdater.ClearAll();
            _smtc.DisplayUpdater.Update();
        }
        catch { }
        SetState(false, NarrationState.Idle);
    }
}
