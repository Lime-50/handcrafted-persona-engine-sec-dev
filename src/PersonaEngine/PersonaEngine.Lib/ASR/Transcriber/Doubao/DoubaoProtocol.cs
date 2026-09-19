using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using PersonaEngine.Lib.Configuration;

namespace PersonaEngine.Lib.ASR.Transcriber.Doubao;

/// <summary>
///     Binary framing helpers for the Volcengine/Doubao big-model streaming ASR
///     v3 WebSocket protocol.
/// </summary>
internal static class DoubaoProtocol
{
    public const int HeaderSize = 4;

    public const int LengthSize = 4;

    public const byte MessageTypeFullClientRequest = 0x1;

    public const byte MessageTypeAudioOnlyRequest = 0x2;

    public const byte MessageTypeFullServerResponse = 0x9;

    public const byte MessageTypeError = 0xF;

    public const byte SerializationJson = 0x1;

    public const byte LastPackageFlag = 0x2;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static byte[] CreateFullClientFrame(ReadOnlySpan<byte> json)
    {
        var frame = new byte[HeaderSize + LengthSize + json.Length];
        frame[0] = 0x11;
        frame[1] = (byte)((MessageTypeFullClientRequest << 4) | 0);
        frame[2] = (byte)((SerializationJson << 4) | 0);
        frame[3] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(HeaderSize, LengthSize), (uint)json.Length);
        json.CopyTo(frame.AsSpan(HeaderSize + LengthSize));

        return frame;
    }

    public static string? ResolveAudioLanguage(DoubaoAsrOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        // Upstream only accepts audio.language on the streaming-input/
        // non-streaming-result endpoint. The bidirectional endpoints use
        // built-in Chinese/English/dialect detection.
        return options.Endpoint.Contains(
            "bigmodel_nostream",
            StringComparison.OrdinalIgnoreCase
        )
            ? options.Language
            : null;
    }

    public static IReadOnlyDictionary<string, string> BuildRequestHeaders(
        DoubaoAsrOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Api-Connect-Id"] = Guid.NewGuid().ToString("D"),
            ["X-Api-Request-Id"] = Guid.NewGuid().ToString("D"),
            ["X-Api-Sequence"] = "-1",
            ["X-Api-Resource-Id"] = RequireHeaderValue(
                options.ResourceId,
                "a resource ID",
                "Config:Asr:Doubao:ResourceId"
            ),
        };

        switch (options.AuthMode)
        {
            case DoubaoAuthMode.ApiKey:
                headers["X-Api-Key"] = RequireHeaderValue(
                    options.ApiKey,
                    "an API key",
                    "Config:Asr:Doubao:ApiKey"
                );

                break;
            case DoubaoAuthMode.AppIdToken:
                headers["X-Api-App-Key"] = RequireHeaderValue(
                    options.AppId,
                    "an App ID",
                    "Config:Asr:Doubao:AppId"
                );
                headers["X-Api-Access-Key"] = RequireHeaderValue(
                    options.AccessToken,
                    "an access token",
                    "Config:Asr:Doubao:AccessToken"
                );

                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported Doubao authentication mode '{options.AuthMode}'."
                );
        }

        return headers;
    }

    public static void ConfigureRequestHeaders(
        ClientWebSocket socket,
        DoubaoAsrOptions options
    )
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(options);

        ConfigureRequestHeaders(socket, BuildRequestHeaders(options));
    }

    public static void ConfigureRequestHeaders(
        ClientWebSocket socket,
        IReadOnlyDictionary<string, string> headers
    )
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(headers);

        foreach (var (name, value) in headers)
        {
            socket.Options.SetRequestHeader(name, value);
        }
    }

    private static string RequireHeaderValue(
        string? value,
        string label,
        string configPath
    )
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            throw new InvalidOperationException(
                $"Doubao streaming ASR requires {label}. Configure {configPath}."
            );
        }

        return trimmed;
    }

    public static byte[] CreateAudioFrame(ReadOnlySpan<byte> pcm, bool isLast)
    {
        var frame = new byte[HeaderSize + LengthSize + pcm.Length];
        frame[0] = 0x11;
        frame[1] = (byte)((MessageTypeAudioOnlyRequest << 4) | (isLast ? LastPackageFlag : 0));
        frame[2] = 0;
        frame[3] = 0;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(HeaderSize, LengthSize), (uint)pcm.Length);
        pcm.CopyTo(frame.AsSpan(HeaderSize + LengthSize));

        return frame;
    }

    public static bool TryReadFrameHeader(ReadOnlySpan<byte> bytes, out DoubaoFrameHeader header)
    {
        if (bytes.Length < HeaderSize)
        {
            header = default;

            return false;
        }

        header = new DoubaoFrameHeader(
            bytes[0],
            bytes[1],
            bytes[2],
            bytes[3]
        );

        return true;
    }

    public static byte[] BuildFullClientRequest(
        DoubaoAsrOptions options,
        string? language,
        uint sampleRate,
        ushort bitsPerSample,
        ushort channelCount
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            writer.WritePropertyName("user");
            writer.WriteStartObject();
            writer.WriteString("uid", string.IsNullOrWhiteSpace(options.UserId) ? "persona-engine" : options.UserId);
            writer.WriteEndObject();

            writer.WritePropertyName("audio");
            writer.WriteStartObject();
            writer.WriteString("format", "pcm");
            writer.WriteNumber("rate", sampleRate);
            writer.WriteNumber("bits", bitsPerSample);
            writer.WriteNumber("channel", channelCount);
            if (!string.IsNullOrWhiteSpace(language))
            {
                writer.WriteString("language", language);
            }

            writer.WriteEndObject();

            writer.WritePropertyName("request");
            writer.WriteStartObject();
            writer.WriteString("model_name", "bigmodel");
            writer.WriteBoolean("show_utterances", options.ShowUtterances);
            writer.WriteString("result_type", string.IsNullOrWhiteSpace(options.ResultType) ? "single" : options.ResultType);
            writer.WriteBoolean("enable_itn", options.EnableItn);
            writer.WriteBoolean("enable_punc", options.EnablePunctuation);
            writer.WriteBoolean("enable_ddc", options.EnableDdc);
            writer.WriteEndObject();

            writer.WriteEndObject();
        }

        return stream.ToArray();
    }

    public static DoubaoServerResponse? ParseServerResponse(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return null;
        }

        var json = Encoding.UTF8.GetString(payload);
        var dto = JsonSerializer.Deserialize<DoubaoResponseDto>(json, JsonOptions);
        if (dto?.Result is null)
        {
            return null;
        }

        var utterances = dto.Result.Utterances is null
            ? Array.Empty<DoubaoUtterance>()
            : dto.Result.Utterances
                .Select(
                    x => new DoubaoUtterance(
                        x.Text ?? string.Empty,
                        TimeSpan.FromMilliseconds(Math.Max(0, x.StartTime)),
                        TimeSpan.FromMilliseconds(Math.Max(0, x.EndTime)),
                        x.Definite
                    )
                )
                .ToArray();

        return new DoubaoServerResponse(dto.Result.Text ?? string.Empty, utterances, dto.AudioInfo?.Duration);
    }

    public static string? ParseErrorMessage(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
        {
            return null;
        }

        var json = Encoding.UTF8.GetString(payload);

        return JsonSerializer.Deserialize<DoubaoErrorDto>(json, JsonOptions)?.Message;
    }

    public readonly record struct DoubaoFrameHeader(
        byte ProtocolAndHeaderSize,
        byte MessageTypeAndFlags,
        byte SerializationAndCompression,
        byte Reserved
    )
    {
        public byte MessageType => (byte)((MessageTypeAndFlags >> 4) & 0x0F);

        public byte Flags => (byte)(MessageTypeAndFlags & 0x0F);

        public byte Serialization => (byte)((SerializationAndCompression >> 4) & 0x0F);

        public byte Compression => (byte)(SerializationAndCompression & 0x0F);
    }

    public sealed record DoubaoServerResponse(
        string Text,
        IReadOnlyList<DoubaoUtterance> Utterances,
        int? AudioDurationMs
    );

    public sealed record DoubaoUtterance(
        string Text,
        TimeSpan StartTime,
        TimeSpan EndTime,
        bool Definite
    )
    {
        public TimeSpan Duration => EndTime - StartTime;
    }

    private sealed class DoubaoResponseDto
    {
        [JsonPropertyName("audio_info")]
        public DoubaoAudioInfoDto? AudioInfo { get; set; }

        [JsonPropertyName("result")]
        public DoubaoResultDto? Result { get; set; }
    }

    private sealed class DoubaoAudioInfoDto
    {
        [JsonPropertyName("duration")]
        public int Duration { get; set; }
    }

    private sealed class DoubaoResultDto
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("utterances")]
        public List<DoubaoUtteranceDto>? Utterances { get; set; }
    }

    private sealed class DoubaoUtteranceDto
    {
        [JsonPropertyName("text")]
        public string? Text { get; set; }

        [JsonPropertyName("start_time")]
        public long StartTime { get; set; }

        [JsonPropertyName("end_time")]
        public long EndTime { get; set; }

        [JsonPropertyName("definite")]
        public bool Definite { get; set; }
    }

    private sealed class DoubaoErrorDto
    {
        [JsonPropertyName("message")]
        public string? Message { get; set; }
    }
}
