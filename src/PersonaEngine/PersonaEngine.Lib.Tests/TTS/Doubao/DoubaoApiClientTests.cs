using System.Text.Json;
using FluentAssertions;
using PersonaEngine.Lib.Configuration;
using PersonaEngine.Lib.TTS.Synthesis.Doubao;
using Xunit;

namespace PersonaEngine.Lib.Tests.TTS.Doubao;

public sealed class DoubaoApiClientTests
{
    [Fact]
    public void BuildRequestBody_SerializesAdditionsAsJsonString()
    {
        var options = new DoubaoTtsOptions
        {
            Language = "zh",
        };

        var body = DoubaoApiClient.BuildRequestBody(
            "hello",
            "zh_female_shuangkuaisisi_uranus_bigtts",
            options
        );

        var reqParams = (Dictionary<string, object?>)body["req_params"]!;
        var additions = reqParams["additions"].Should().BeOfType<string>().Subject;

        using var document = JsonDocument.Parse(additions);
        document.RootElement.GetProperty("explicit_language").GetString().Should().Be("zh");
    }

    [Fact]
    public void BuildRequestBody_AddsSubtitleOnlyForV2Voice()
    {
        var options = new DoubaoTtsOptions
        {
            EnableSubtitle = true,
        };

        var v2Audio = GetAudioParams(
            DoubaoApiClient.BuildRequestBody(
                "hello",
                "zh_female_shuangkuaisisi_uranus_bigtts",
                options
            )
        );
        var v1Audio = GetAudioParams(
            DoubaoApiClient.BuildRequestBody("hello", "BV120_streaming", options)
        );

        v2Audio.Should().ContainKey("enable_subtitle");
        v1Audio.Should().NotContainKey("enable_subtitle");
    }

    [Fact]
    public void BuildRequestHeaders_ApiKeyMode_TrimsAndUsesXApiKey()
    {
        var options = new DoubaoTtsOptions
        {
            AuthMode = DoubaoTtsAuthMode.ApiKey,
            ApiKey = "  test-api-key  ",
            ResourceId = "  seed-tts-2.0  ",
        };

        var headers = DoubaoApiClient.BuildRequestHeaders(options);

        headers["X-Api-Key"].Should().Be("test-api-key");
        headers["X-Api-Resource-Id"].Should().Be("seed-tts-2.0");
        headers.Should().NotContainKey("X-Api-App-Id");
        headers.Should().NotContainKey("X-Api-Access-Key");
    }

    [Fact]
    public void BuildRequestHeaders_AppIdAccessKeyMode_UsesLegacyHeaders()
    {
        var options = new DoubaoTtsOptions
        {
            AuthMode = DoubaoTtsAuthMode.AppIdAccessKey,
            AppId = "test-app-id",
            AccessKey = "test-access-key",
            ResourceId = "seed-tts-2.0",
        };

        var headers = DoubaoApiClient.BuildRequestHeaders(options);

        headers["X-Api-App-Id"].Should().Be("test-app-id");
        headers["X-Api-Access-Key"].Should().Be("test-access-key");
        headers.Should().NotContainKey("X-Api-Key");
    }

    [Fact]
    public void BuildRequestHeaders_RequiresCredentialsForSelectedMode()
    {
        var apiKeyOptions = new DoubaoTtsOptions
        {
            AuthMode = DoubaoTtsAuthMode.ApiKey,
            ApiKey = string.Empty,
        };
        var legacyOptions = new DoubaoTtsOptions
        {
            AuthMode = DoubaoTtsAuthMode.AppIdAccessKey,
            AppId = "test-app-id",
            AccessKey = string.Empty,
        };

        Action apiKeyCall = () => DoubaoApiClient.BuildRequestHeaders(apiKeyOptions);
        Action legacyCall = () => DoubaoApiClient.BuildRequestHeaders(legacyOptions);

        apiKeyCall.Should().Throw<InvalidOperationException>().WithMessage("*API key*");
        legacyCall.Should().Throw<InvalidOperationException>().WithMessage("*Access Key*");
    }

    [Fact]
    public void BuildRequestBody_TrimsVoiceIdAndStillDetectsVoiceFamily()
    {
        // Voice ids pasted from the console frequently carry stray whitespace; the
        // speaker field must be normalised or the API rejects the request.
        var options = new DoubaoTtsOptions { EnableSubtitle = true };

        var body = DoubaoApiClient.BuildRequestBody(
            "hello",
            "  zh_female_shuangkuaisisi_uranus_bigtts  ",
            options
        );

        var reqParams = (Dictionary<string, object?>)body["req_params"]!;
        reqParams["speaker"].Should().Be("zh_female_shuangkuaisisi_uranus_bigtts");

        // Trimming must happen before the 2.0-voice check, otherwise enable_subtitle
        // would be dropped for a padded id.
        GetAudioParams(body).Should().ContainKey("enable_subtitle");
    }

    [Fact]
    public void BuildRequestBody_CustomVoiceId_IsSentVerbatim()
    {
        // Cloned voices use ids such as "S_xxxxxxx" and are not in the bundled catalog.
        var body = DoubaoApiClient.BuildRequestBody("hello", "S_1a2b3c4d", new DoubaoTtsOptions());

        var reqParams = (Dictionary<string, object?>)body["req_params"]!;
        reqParams["speaker"].Should().Be("S_1a2b3c4d");
    }

    private static Dictionary<string, object?> GetAudioParams(
        Dictionary<string, object?> body
    )
    {
        var reqParams = (Dictionary<string, object?>)body["req_params"]!;
        return (Dictionary<string, object?>)reqParams["audio_params"]!;
    }
}
