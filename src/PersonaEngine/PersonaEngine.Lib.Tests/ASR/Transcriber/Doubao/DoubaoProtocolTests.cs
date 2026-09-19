using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using FluentAssertions;
using PersonaEngine.Lib.ASR.Transcriber.Doubao;
using PersonaEngine.Lib.Configuration;
using Xunit;

namespace PersonaEngine.Lib.Tests.ASR.Transcriber.Doubao;

public sealed class DoubaoProtocolTests
{
    [Fact]
    public void CreateFullClientFrame_WritesHeaderJsonLengthAndPayload()
    {
        var json = Encoding.UTF8.GetBytes("{}");

        var frame = DoubaoProtocol.CreateFullClientFrame(json);

        frame.Should().HaveCount(DoubaoProtocol.HeaderSize + DoubaoProtocol.LengthSize + json.Length);
        frame[0].Should().Be(0x11);
        frame[1].Should().Be(0x10);
        frame[2].Should().Be(0x10);
        frame[3].Should().Be(0x00);
        BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(4, 4)).Should().Be((uint)json.Length);
        frame.AsSpan(8).ToArray().Should().Equal(json);
    }

    [Fact]
    public void CreateAudioFrame_LastPackageFlagIsOnlySetWhenRequested()
    {
        var pcm = new byte[] { 1, 2, 3 };

        var normal = DoubaoProtocol.CreateAudioFrame(pcm, isLast: false);
        var final = DoubaoProtocol.CreateAudioFrame(pcm, isLast: true);

        normal[1].Should().Be(0x20);
        final[1].Should().Be(0x22);
        BinaryPrimitives.ReadUInt32BigEndian(final.AsSpan(4, 4)).Should().Be((uint)pcm.Length);
    }

    [Fact]
    public void TryReadFrameHeader_ReturnsFalseWhenHeaderIsTooShort()
    {
        var couldRead = DoubaoProtocol.TryReadFrameHeader(
            [0x11, 0x10, 0x10],
            out var header
        );

        couldRead.Should().BeFalse();
        header.MessageType.Should().Be(0);
    }

    [Fact]
    public void BuildFullClientRequest_ContainsExpectedFields()
    {
        var options = new DoubaoAsrOptions
        {
            UserId = "test-user",
            Language = "zh-CN",
            ResultType = "single",
            ShowUtterances = true,
            EnableItn = true,
            EnablePunctuation = true,
            EnableDdc = false,
        };

        var json = Encoding.UTF8.GetString(
            DoubaoProtocol.BuildFullClientRequest(
                options,
                language: options.Language,
                sampleRate: 16000,
                bitsPerSample: 16,
                channelCount: 1
            )
        );

        json.Should().Contain("\"uid\":\"test-user\"");
        json.Should().Contain("\"language\":\"zh-CN\"");
        json.Should().Contain("\"model_name\":\"bigmodel\"");
        json.Should().Contain("\"show_utterances\":true");
        json.Should().Contain("\"enable_punc\":true");
    }

    [Fact]
    public void BuildRequestHeaders_ApiKeyMode_UsesNewConsoleHeader()
    {
        var options = new DoubaoAsrOptions
        {
            AuthMode = DoubaoAuthMode.ApiKey,
            ApiKey = "  test-api-key  ",
            ResourceId = "  volc.seedasr.sauc.duration  ",
        };

        var headers = DoubaoProtocol.BuildRequestHeaders(options);

        headers["X-Api-Key"].Should().Be("test-api-key");
        headers["X-Api-Resource-Id"].Should().Be("volc.seedasr.sauc.duration");
        headers["X-Api-Sequence"].Should().Be("-1");
        headers.Should().NotContainKey("X-Api-App-Key");
        headers.Should().NotContainKey("X-Api-Access-Key");
        AssertConnectionIdentifiers(headers);
    }

    [Fact]
    public void BuildRequestHeaders_AppIdTokenMode_UsesLegacyHeaders()
    {
        var options = new DoubaoAsrOptions
        {
            AuthMode = DoubaoAuthMode.AppIdToken,
            AppId = "  test-app-id  ",
            AccessToken = "  test-access-token  ",
        };

        var headers = DoubaoProtocol.BuildRequestHeaders(options);

        headers["X-Api-App-Key"].Should().Be("test-app-id");
        headers["X-Api-Access-Key"].Should().Be("test-access-token");
        headers.Should().NotContainKey("X-Api-Key");
        AssertConnectionIdentifiers(headers);
    }

    [Fact]
    public void BuildRequestHeaders_RequiresCredentialsForSelectedMode()
    {
        var apiKeyOptions = new DoubaoAsrOptions
        {
            AuthMode = DoubaoAuthMode.ApiKey,
            ApiKey = string.Empty,
        };
        var legacyOptions = new DoubaoAsrOptions
        {
            AuthMode = DoubaoAuthMode.AppIdToken,
            AppId = "test-app-id",
            AccessToken = string.Empty,
        };

        var apiKeyCall = () => DoubaoProtocol.BuildRequestHeaders(apiKeyOptions);
        var legacyCall = () => DoubaoProtocol.BuildRequestHeaders(legacyOptions);

        apiKeyCall.Should().Throw<InvalidOperationException>().WithMessage("*API key*");
        legacyCall.Should().Throw<InvalidOperationException>().WithMessage("*access token*");
    }

    [Fact]
    public void ParseServerResponse_ReadsUtterancesAndText()
    {
        const string json =
            """
            {
              "audio_info": { "duration": 1234 },
              "result": {
                "text": "你好世界",
                "utterances": [
                  { "text": "你好", "start_time": 0, "end_time": 300, "definite": false },
                  { "text": "你好世界", "start_time": 0, "end_time": 800, "definite": true }
                ]
              }
            }
            """;

        var response = DoubaoProtocol.ParseServerResponse(
            Encoding.UTF8.GetBytes(json)
        );

        response.Should().NotBeNull();
        response!.Text.Should().Be("你好世界");
        response.AudioDurationMs.Should().Be(1234);
        response.Utterances.Should().HaveCount(2);
        response.Utterances[0].Definite.Should().BeFalse();
        response.Utterances[1].Definite.Should().BeTrue();
        response.Utterances[1].StartTime.TotalMilliseconds.Should().Be(0);
        response.Utterances[1].EndTime.TotalMilliseconds.Should().Be(800);
        response.Utterances[1].Duration.TotalMilliseconds.Should().Be(800);
    }

    private static void AssertConnectionIdentifiers(
        IReadOnlyDictionary<string, string> headers
    )
    {
        headers.Should().ContainKey("X-Api-Connect-Id");
        headers.Should().ContainKey("X-Api-Request-Id");
        headers["X-Api-Connect-Id"].Should().NotBe(headers["X-Api-Request-Id"]);
        Guid.TryParse(headers["X-Api-Connect-Id"], out _).Should().BeTrue();
        Guid.TryParse(headers["X-Api-Request-Id"], out _).Should().BeTrue();
    }
}
