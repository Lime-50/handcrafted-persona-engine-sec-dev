using System.Globalization;
using PersonaEngine.Lib.ASR.Transcriber;
using Xunit;

namespace PersonaEngine.Lib.Tests.ASR.Transcriber;

public class WhisperNetSupportedLanguageTests
{
    [Theory]
    [InlineData("en-US")]
    [InlineData("zh-CN")]
    [InlineData("yue")]
    [InlineData("ja-JP")]
    [InlineData("ko-KR")]
    [InlineData("ar-SA")]
    public void IsSupported_CommonUiCultures(string cultureName)
    {
        Assert.True(
            WhisperNetSupportedLanguage.IsSupported(CultureInfo.GetCultureInfo(cultureName)),
            $"'{cultureName}' must stay selectable in the language picker"
        );
    }
}
