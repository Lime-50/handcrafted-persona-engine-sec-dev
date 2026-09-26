using System.Drawing;
using FontStashSharp;
using Microsoft.Extensions.Logging;
using PersonaEngine.Lib.UI.Common;
using PersonaEngine.Lib.UI.Rendering.Text;
using Silk.NET.OpenGL;
using StbImageSharp;
using Texture = PersonaEngine.Lib.UI.Common.Texture;

namespace PersonaEngine.Lib.UI;

/// <summary>
///     Manages caching and loading of fonts and textures.
/// </summary>
public class FontProvider : IStartupTask
{
    private const string FONTS_DIR = @"Resources\Fonts";

    private const string IMAGES_PATH = @"Resources\Imgs";

    private readonly Dictionary<string, FontSystem> _fontCache = new();

    private readonly string _fontsDirectory;

    private readonly ILogger<FontProvider> _logger;

    private readonly Dictionary<string, Texture> _textureCache = new();

    private Texture2DManager _texture2DManager = null!;

    // static FontProvider()
    // {
    //     FontSystemDefaults.FontLoader = new SixLaborsFontLoader();
    // }

    public FontProvider(ILogger<FontProvider> logger)
        : this(logger, FONTS_DIR)
    {
    }

    internal FontProvider(ILogger<FontProvider> logger, string fontsDirectory)
    {
        _logger = logger;
        _fontsDirectory = string.IsNullOrWhiteSpace(fontsDirectory)
            ? FONTS_DIR
            : fontsDirectory;
    }

    public void Execute(GL gl)
    {
        _texture2DManager = new Texture2DManager(gl);
    }

    public IReadOnlyList<string> GetAvailableFonts()
    {
        if (!Directory.Exists(_fontsDirectory))
        {
            _logger.LogWarning("Fonts directory not found: {Path}", _fontsDirectory);
            return [];
        }

        var fontFiles = EnumerateFontFiles().ToArray();
        var fontNames = new string[fontFiles.Length];
        for (var i = 0; i < fontFiles.Length; i++)
        {
            fontNames[i] = Path.GetFileName(fontFiles[i]);
        }

        return fontNames;
    }

    public Task<IReadOnlyList<string>> GetAvailableFontsAsync(
        CancellationToken cancellationToken = default
    )
    {
        try
        {
            if (!Directory.Exists(_fontsDirectory))
            {
                _logger.LogWarning("Fonts directory not found: {Path}", _fontsDirectory);

                return Task.FromResult<IReadOnlyList<string>>(Array.Empty<string>());
            }

            var fontFiles = EnumerateFontFiles().ToArray();
            var fontNames = new List<string>(fontFiles.Length);
            foreach (var file in fontFiles)
            {
                var voiceId = Path.GetFileName(file);
                fontNames.Add(voiceId);
            }

            _logger.LogInformation("Found {Count} available fonts", fontNames.Count);

            return Task.FromResult<IReadOnlyList<string>>(fontNames);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error getting available fonts");

            throw;
        }
    }

    public FontSystem GetFontSystem(string fontName)
    {
        if (!_fontCache.TryGetValue(fontName, out var fontSystem))
        {
            fontSystem = new FontSystem();
            var fontData = File.ReadAllBytes(Path.Combine(_fontsDirectory, fontName));
            fontSystem.AddFont(fontData);

            AddCjkFallback(fontSystem, fontName);

            _fontCache[fontName] = fontSystem;
        }

        return fontSystem;
    }

    public string GetFontsDirectory() => Path.GetFullPath(_fontsDirectory);

    /// <summary>
    ///     Copies a user-provided .ttf/.otf file into the managed fonts folder
    ///     and returns the file name to persist in <c>SubtitleOptions.Font</c>.
    /// </summary>
    public string ImportFont(string sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            throw new ArgumentException("Font path is required.", nameof(sourcePath));
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("The selected font file was not found.", sourcePath);
        }

        if (!IsSupportedFontFile(sourcePath))
        {
            throw new NotSupportedException(
                $"Only .ttf and .otf fonts are supported: {Path.GetFileName(sourcePath)}"
            );
        }

        Directory.CreateDirectory(_fontsDirectory);

        var sourceFullPath = Path.GetFullPath(sourcePath);
        var fileName = Path.GetFileName(sourcePath);
        var targetPath = Path.Combine(_fontsDirectory, fileName);

        if (
            string.Equals(
                sourceFullPath,
                Path.GetFullPath(targetPath),
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            return fileName;
        }

        targetPath = EnsureUniqueFontPath(targetPath);
        fileName = Path.GetFileName(targetPath);
        File.Copy(sourceFullPath, targetPath, overwrite: false);
        _fontCache.Remove(fileName);

        _logger.LogInformation(
            "Imported font '{Source}' as '{Destination}'",
            sourcePath,
            targetPath
        );

        return fileName;
    }

    internal static bool IsSupportedFontFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".ttf", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".otf", StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<string> EnumerateFontFiles() =>
        Directory
            .EnumerateFiles(_fontsDirectory, "*.*", SearchOption.TopDirectoryOnly)
            .Where(IsSupportedFontFile)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);

    private static string EnsureUniqueFontPath(string targetPath)
    {
        if (!File.Exists(targetPath))
        {
            return targetPath;
        }

        var directory = Path.GetDirectoryName(targetPath) ?? string.Empty;
        var fileName = Path.GetFileNameWithoutExtension(targetPath);
        var extension = Path.GetExtension(targetPath);

        for (var index = 1; index < 10_000; index++)
        {
            var candidate = Path.Combine(directory, $"{fileName} ({index}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new IOException($"Could not find a unique file name for '{fileName}'.");
    }

    /// <summary>
    ///     Adds a CJK-capable font to the same <see cref="FontSystem" /> so glyphs the
    ///     primary font lacks (Chinese, Japanese, Korean) fall back to it instead of
    ///     rendering as '?'. FontStashSharp resolves glyphs across all added fonts.
    /// </summary>
    private void AddCjkFallback(FontSystem fontSystem, string primaryFontName)
    {
        var cjkPath = CjkFontResolver.TryResolve(allowTtc: false);
        if (cjkPath is null)
        {
            _logger.LogDebug(
                "No CJK fallback font found; non-Latin glyphs in '{Font}' may render as '?'",
                primaryFontName
            );

            return;
        }

        try
        {
            fontSystem.AddFont(File.ReadAllBytes(cjkPath));
            _logger.LogDebug(
                "Added CJK fallback font '{Path}' to '{Font}'",
                cjkPath,
                primaryFontName
            );
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to load CJK fallback font '{Path}'; non-Latin glyphs in '{Font}' may render as '?'",
                cjkPath,
                primaryFontName
            );
        }
    }

    public Texture GetTexture(string imageName)
    {
        if (!_textureCache.TryGetValue(imageName, out var texture))
        {
            using var stream = File.OpenRead(Path.Combine(IMAGES_PATH, imageName));
            var imageResult = ImageResult.FromStream(stream, ColorComponents.RedGreenBlueAlpha);

            // Premultiply alpha
            unsafe
            {
                fixed (byte* b = imageResult.Data)
                {
                    var ptr = b;
                    for (var i = 0; i < imageResult.Data.Length; i += 4, ptr += 4)
                    {
                        var falpha = ptr[3] / 255.0f;
                        ptr[0] = (byte)(ptr[0] * falpha);
                        ptr[1] = (byte)(ptr[1] * falpha);
                        ptr[2] = (byte)(ptr[2] * falpha);
                    }
                }
            }

            texture = (Texture)
                _texture2DManager.CreateTexture(imageResult.Width, imageResult.Height);
            _texture2DManager.SetTextureData(
                texture,
                new Rectangle(0, 0, (int)texture.Width, (int)texture.Height),
                imageResult.Data
            );
            _textureCache[imageName] = texture;
        }

        return texture;
    }
}
