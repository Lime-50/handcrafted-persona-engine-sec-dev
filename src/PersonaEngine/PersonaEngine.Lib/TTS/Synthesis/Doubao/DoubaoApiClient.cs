using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PersonaEngine.Lib.Configuration;

namespace PersonaEngine.Lib.TTS.Synthesis.Doubao;

/// <summary>
///     Thin HTTP client for the Doubao (Volcengine) V3 SSE unidirectional
///     streaming speech synthesis API. Streams one sentence at a time and
///     yields base64 audio chunks plus optional word-level timestamps.
/// </summary>
public sealed class DoubaoApiClient
{
    public const string HttpClientName = "doubao-tts";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<DoubaoApiClient> _logger;
    private readonly IOptionsMonitor<DoubaoTtsOptions> _options;

    public DoubaoApiClient(
        IHttpClientFactory httpClientFactory,
        IOptionsMonitor<DoubaoTtsOptions> options,
        ILogger<DoubaoApiClient> logger
    )
    {
        _httpClientFactory = httpClientFactory;
        _options = options;
        _logger = logger;
    }

    /// <summary>
    ///     Synthesizes <paramref name="text" /> into a stream of audio chunks
    ///     (and optional sentence timestamps). One HTTP request per sentence.
    /// </summary>
    public async IAsyncEnumerable<DoubaoSynthesisEvent> SynthesizeAsync(
        string text,
        string voice,
        DoubaoTtsOptions options,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);
        request.Headers.TryAddWithoutValidation("X-Api-Resource-Id", options.ResourceId);
        request.Headers.TryAddWithoutValidation("X-Api-Request-Id", Guid.NewGuid().ToString());
        request.Headers.TryAddWithoutValidation("Accept", "text/event-stream");

        if (!string.IsNullOrWhiteSpace(options.ApiKey))
        {
            request.Headers.TryAddWithoutValidation("X-Api-Key", options.ApiKey);
        }
        else
        {
            request.Headers.TryAddWithoutValidation("X-Api-App-Id", options.AppId);
            request.Headers.TryAddWithoutValidation("X-Api-Access-Key", options.AccessKey);
        }

        request.Content = JsonContent.Create(BuildRequestBody(text, voice, options));

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, options.TimeoutSeconds)));

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var response = await client
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            throw new DoubaoApiException(
                $"Doubao TTS HTTP {(int)response.StatusCode} {response.ReasonPhrase}: {detail}"
            );
        }

        await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token).ConfigureAwait(false);
        using var reader = new StreamReader(stream, Encoding.UTF8);

        string? line;
        while ((line = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(':'))
            {
                continue; // SSE comment / blank separator
            }

            if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var payload = line.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                ? line["data:".Length..].TrimStart()
                : line;

            if (payload == "[DONE]")
            {
                yield break;
            }

            if (!payload.StartsWith('{'))
            {
                continue;
            }

            var evt = JsonSerializer.Deserialize<DoubaoStreamPayload>(payload, JsonOpts);
            if (evt is null)
            {
                continue;
            }

            if (evt.Code is not (0 or 20000000))
            {
                _logger.LogWarning(
                    "Doubao TTS returned error code {Code}: {Message} (text: '{Text}')",
                    evt.Code,
                    evt.Message,
                    text
                );
                throw new DoubaoApiException(evt.Code, evt.Message ?? "unknown error");
            }

            var audio = evt.Data is null ? null : Convert.FromBase64String(evt.Data);
            yield return new DoubaoSynthesisEvent(audio, evt.Sentence);

            if (evt.Code == 20000000)
            {
                yield break;
            }
        }
    }

    private static Dictionary<string, object?> BuildRequestBody(
        string text,
        string voice,
        DoubaoTtsOptions options
    )
    {
        var audioParams = new Dictionary<string, object?>
        {
            ["format"] = options.Format,
            ["sample_rate"] = options.SampleRate,
            ["speech_rate"] = options.SpeechRate,
            ["loudness_rate"] = options.LoudnessRate,
        };

        if (!string.IsNullOrWhiteSpace(options.Emotion))
        {
            audioParams["emotion"] = options.Emotion;
            audioParams["emotion_scale"] = options.EmotionScale;
        }

        // enable_timestamp applies to TTS 1.0 voices, enable_subtitle to 2.0 voices.
        // Send only the flag matching the voice family to avoid API-side surprises.
        var isV2Voice = voice.EndsWith("_uranus_bigtts", StringComparison.OrdinalIgnoreCase);
        if (options.EnableTimestamp && !isV2Voice)
        {
            audioParams["enable_timestamp"] = true;
        }

        if (options.EnableSubtitle && isV2Voice)
        {
            audioParams["enable_subtitle"] = true;
        }

        var additions = new Dictionary<string, object?>();
        if (!string.IsNullOrWhiteSpace(options.Language))
        {
            additions["explicit_language"] = options.Language;
        }

        return new Dictionary<string, object?>
        {
            ["user"] = new Dictionary<string, object?> { ["uid"] = options.Uid },
            ["req_params"] = new Dictionary<string, object?>
            {
                ["text"] = text,
                ["speaker"] = voice,
                ["audio_params"] = audioParams,
                ["additions"] = additions,
            },
        };
    }

    /// <summary>One SSE payload. <c>data</c> is base64 audio; <c>sentence</c> carries optional timestamps.</summary>
    private sealed record DoubaoStreamPayload
    {
        public int Code { get; init; }
        public string? Message { get; init; }
        public string? Data { get; init; }
        public DoubaoSentence? Sentence { get; init; }
    }
}

/// <summary>A decoded SSE event: an audio chunk and/or word-level timing for the sentence.</summary>
public sealed record DoubaoSynthesisEvent(byte[]? Audio, DoubaoSentence? Sentence);

/// <summary>Word-level timing returned by the API when timestamps/subtitles are enabled.</summary>
public sealed record DoubaoSentence
{
    public string Text { get; init; } = string.Empty;
    public IReadOnlyList<DoubaoWord> Words { get; init; } = [];
}

public sealed record DoubaoWord
{
    public string Word { get; init; } = string.Empty;
    public double StartTime { get; init; }
    public double EndTime { get; init; }
    public double Confidence { get; init; }
}
