namespace PdfReader.Core;

/// <summary>How rendered PDF pages are recoloured to match the app theme.</summary>
public enum PageTint { None, Sepia, Dark }

/// <summary>
/// Recolours page pixels (BGRA, 4 bytes per pixel). Sepia maps white paper to a warm paper colour and
/// black ink to dark brown. Dark swaps light and dark but keeps colours roughly their hue, so blue links
/// stay blue and white pages become dark grey with light text.
/// </summary>
public static class PageTinter
{
    // Sepia end points (also the Sepia theme's page colour).
    public static readonly (byte R, byte G, byte B) SepiaPaper = (0xF4, 0xEC, 0xD8);
    public static readonly (byte R, byte G, byte B) SepiaInk = (0x3B, 0x2F, 0x22);

    // Dark: white becomes DarkPaper, black becomes DarkInk.
    public const byte DarkPaper = 0x1E;
    public const byte DarkInk = 0xDE;

    private static readonly byte[] SepiaR = Lut(SepiaInk.R, SepiaPaper.R);
    private static readonly byte[] SepiaG = Lut(SepiaInk.G, SepiaPaper.G);
    private static readonly byte[] SepiaB = Lut(SepiaInk.B, SepiaPaper.B);

    private static byte[] Lut(byte from, byte to)
    {
        var lut = new byte[256];
        for (int v = 0; v < 256; v++) lut[v] = (byte)(from + (to - from) * v / 255);
        return lut;
    }

    public static void Apply(Span<byte> bgra, PageTint tint)
    {
        if (tint == PageTint.None) return;
        for (int i = 0; i + 3 < bgra.Length; i += 4)
        {
            var (r, g, b) = Map(tint, bgra[i + 2], bgra[i + 1], bgra[i]);
            bgra[i] = b;
            bgra[i + 1] = g;
            bgra[i + 2] = r;
        }
    }

    public static (byte R, byte G, byte B) Map(PageTint tint, byte r, byte g, byte b)
    {
        switch (tint)
        {
            case PageTint.Sepia:
                return (SepiaR[r], SepiaG[g], SepiaB[b]);
            case PageTint.Dark:
                int y = (77 * r + 150 * g + 29 * b) >> 8;                      // brightness
                int y2 = DarkPaper + (DarkInk - DarkPaper) * (255 - y) / 255;   // flipped brightness
                return (Clamp(y2 + (r - y) * 7 / 8), Clamp(y2 + (g - y) * 7 / 8), Clamp(y2 + (b - y) * 7 / 8));
            default:
                return (r, g, b);
        }
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
