using PersonaEngine.Lib.UI.ControlPanel.Panels.Voice;
using Xunit;

namespace PersonaEngine.Lib.Tests.UI.ControlPanel.Voice;

public sealed class VoiceModeMappingTests
{
    [Theory]
    [InlineData("kokoro", VoiceMode.Clear)]
    [InlineData("Kokoro", VoiceMode.Clear)]
    [InlineData("qwen3", VoiceMode.Expressive)]
    [InlineData("doubao", VoiceMode.Doubao)]
    [InlineData("Doubao", VoiceMode.Doubao)]
    [InlineData("", VoiceMode.Clear)]
    [InlineData("not-a-real-engine", VoiceMode.Clear)]
    public void FromEngineId_Maps_Correctly(string id, VoiceMode expected) =>
        Assert.Equal(expected, VoiceModeMapping.FromEngineId(id));

    [Theory]
    [InlineData(VoiceMode.Clear, "kokoro")]
    [InlineData(VoiceMode.Expressive, "qwen3")]
    [InlineData(VoiceMode.Doubao, "doubao")]
    public void ToEngineId_Maps_Correctly(VoiceMode mode, string expected) =>
        Assert.Equal(expected, VoiceModeMapping.ToEngineId(mode));
}
