namespace PersonaEngine.Lib.UI;

/// <summary>
///     Resolves a CJK-capable font file so Chinese (and other East Asian) text renders
///     instead of the missing-glyph '?'. Priority: fonts bundled under Resources\Fonts,
///     then Windows system fonts (SimHei, DengXian, SimSun, FangSong, KaiTi).
///     <para>
///         .ttc collection fonts are only offered when <paramref name="allowTtc" /> is
///         true: ImGui's built-in loader supports them, but FontStashSharp's default
///         StbTrueTypeSharp loader throws on stbtt_InitFont, so the subtitle/roulette
///         path must stick to plain .ttf/.otf files.
///     </para>
/// </summary>
internal static class CjkFontResolver
{
    private const string FontsDir = @"Resources\Fonts";

    private const string WindowsFontsDir = @"C:\Windows\Fonts";

    // Names a user may drop into Resources\Fonts; checked before system fonts so a
    // bundled Noto / Source Han font wins over the Windows defaults.
    private static readonly string[] BundledCandidates =
    [
        "NotoSansSC-Regular.ttf",
        "NotoSansCJK-Regular.ttc",
        "SourceHanSansCN-Regular.otf",
        "msyh.ttc",
        "simhei.ttf",
        "Deng.ttf",
    ];

    private static readonly string[] SystemTtfCandidates =
    [
        "simhei.ttf",
        "Deng.ttf",
        "simsunb.ttf",
        "simfang.ttf",
        "simkai.ttf",
    ];

    private static readonly string[] SystemTtcCandidates =
    [
        "msyh.ttc",
        "simsun.ttc",
    ];

    public static string? TryResolve(bool allowTtc = true)
    {
        foreach (var name in BundledCandidates)
        {
            if (TryGet(name, allowTtc, out var path))
            {
                return path;
            }
        }

        foreach (var name in SystemTtfCandidates)
        {
            if (TryGet(Path.Combine(WindowsFontsDir, name), allowTtc, out var path))
            {
                return path;
            }
        }

        if (allowTtc)
        {
            foreach (var name in SystemTtcCandidates)
            {
                if (TryGet(Path.Combine(WindowsFontsDir, name), allowTtc, out var path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    private static bool TryGet(string pathOrName, bool allowTtc, out string path)
    {
        path = Path.IsPathRooted(pathOrName) ? pathOrName : Path.Combine(FontsDir, pathOrName);
        if (!File.Exists(path))
        {
            return false;
        }

        var extension = Path.GetExtension(path);
        if (
            !allowTtc
            && string.Equals(extension, ".ttc", StringComparison.OrdinalIgnoreCase)
        )
        {
            return false;
        }

        return true;
    }
}
