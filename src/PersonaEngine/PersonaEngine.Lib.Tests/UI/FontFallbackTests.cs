using FontStashSharp;
using PersonaEngine.Lib.UI;
using Xunit;

namespace PersonaEngine.Lib.Tests.UI;

public class FontFallbackTests
{
    /// <summary>
    ///     DynaPuff has no CJK glyphs, so "你好" measures ~8px (missing-glyph width).
    ///     After adding a CJK font to the same FontSystem, FontStashSharp falls back per
    ///     glyph and the same string measures ~48px (two full-width ideographs).
    /// </summary>
    [Fact]
    public void CjkFallback_WidensChineseMeasurement()
    {
        var dynaPath = FindRepoFont(
            Path.Combine("PersonaEngine.Lib", "Resources", "Fonts", "DynaPuff.ttf")
        );
        var cjkPath = CjkFontResolver.TryResolve(allowTtc: false);

        // Fonts are environment-dependent; skip (pass) on machines without them.
        if (dynaPath is null || cjkPath is null)
        {
            return;
        }

        using var latinOnly = new FontSystem();
        latinOnly.AddFont(File.ReadAllBytes(dynaPath));
        var latinWidth = latinOnly.GetFont(24f).MeasureString("你好").X;

        using var withFallback = new FontSystem();
        withFallback.AddFont(File.ReadAllBytes(dynaPath));
        withFallback.AddFont(File.ReadAllBytes(cjkPath));
        var fallbackWidth = withFallback.GetFont(24f).MeasureString("你好").X;

        Assert.True(
            fallbackWidth > latinWidth,
            $"CJK fallback should replace missing glyphs (latin-only={latinWidth}, fallback={fallbackWidth})"
        );
    }

    private static string? FindRepoFont(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
