using Microsoft.Extensions.Logging;
using NSubstitute;
using PersonaEngine.Lib.UI;
using Xunit;

namespace PersonaEngine.Lib.Tests.UI;

public sealed class FontProviderTests
{
    [Theory]
    [InlineData("font.ttf", true)]
    [InlineData("font.otf", true)]
    [InlineData("font.TTF", true)]
    [InlineData("font.woff2", false)]
    [InlineData("font.txt", false)]
    public void IsSupportedFontFile_ValidatesExtensions(string path, bool expected)
    {
        Assert.Equal(expected, FontProvider.IsSupportedFontFile(path));
    }

    [Fact]
    public void GetAvailableFonts_ScansTtfAndOtf()
    {
        var root = CreateTempDirectory();
        try
        {
            File.WriteAllBytes(Path.Combine(root, "b.otf"), [0x00]);
            File.WriteAllBytes(Path.Combine(root, "a.ttf"), [0x00]);
            File.WriteAllBytes(Path.Combine(root, "ignore.txt"), [0x00]);

            var provider = CreateProvider(root);

            var fonts = provider.GetAvailableFonts();

            Assert.Equal(["a.ttf", "b.otf"], fonts);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void ImportFont_CopiesIntoManagedFolderAndAvoidsOverwrite()
    {
        var sourceRoot = CreateTempDirectory();
        var fontsRoot = CreateTempDirectory();
        try
        {
            var source = Path.Combine(sourceRoot, "Custom Font.ttf");
            File.WriteAllBytes(source, [0x01, 0x02]);
            var provider = CreateProvider(fontsRoot);

            var first = provider.ImportFont(source);
            var second = provider.ImportFont(source);

            Assert.Equal("Custom Font.ttf", first);
            Assert.Equal("Custom Font (1).ttf", second);
            Assert.True(File.Exists(Path.Combine(fontsRoot, first)));
            Assert.True(File.Exists(Path.Combine(fontsRoot, second)));
        }
        finally
        {
            Directory.Delete(sourceRoot, recursive: true);
            Directory.Delete(fontsRoot, recursive: true);
        }
    }

    private static FontProvider CreateProvider(string fontsDirectory) =>
        new(
            Substitute.For<ILogger<FontProvider>>(),
            fontsDirectory
        );

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(
            Path.GetTempPath(),
            "persona-engine-font-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(path);
        return path;
    }
}
