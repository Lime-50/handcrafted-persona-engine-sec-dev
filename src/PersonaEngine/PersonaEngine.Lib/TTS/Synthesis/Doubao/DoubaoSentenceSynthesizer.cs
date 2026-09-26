using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PersonaEngine.Lib.Configuration;
using PersonaEngine.Lib.TTS.Synthesis.Engine;

namespace PersonaEngine.Lib.TTS.Synthesis.Doubao;

/// <summary>
///     Doubao (Volcengine) cloud TTS sentence synthesizer.
///     One HTTP SSE request per sentence; decodes the returned audio into
///     float PCM segments and applies best-effort token timing for subtitles
///     and lip sync (proportional fallback when the API has no word timestamps).
/// </summary>
internal sealed class DoubaoSentenceSynthesizer : ISentenceSynthesizer
{
    /// <summary>
    ///     Volcengine result code returned when the request carried nothing pronounceable.
    ///     Treated as "nothing to say" rather than a failure so one odd fragment cannot
    ///     abort the whole reply.
    /// </summary>
    private const int NoReadableTextCode = 45002001;

    private readonly DoubaoApiClient _client;
    private readonly ILogger<DoubaoSentenceSynthesizer> _logger;
    private readonly IOptionsMonitor<DoubaoTtsOptions> _options;

    public DoubaoSentenceSynthesizer(
        DoubaoApiClient client,
        IOptionsMonitor<DoubaoTtsOptions> options,
        ILogger<DoubaoSentenceSynthesizer> logger
    )
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    public string EngineId => "doubao";

    public TtsEngineCapabilities Capabilities => TtsEngineCapabilities.SpeedControl;

    public ISynthesisSession CreateSession() => new DoubaoSynthesisSession(this, null);

    public ISynthesisSession CreateSession(string voiceName) =>
        new DoubaoSynthesisSession(this, voiceName);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    internal async IAsyncEnumerable<AudioSegment> SynthesizeCoreAsync(
        string sentence,
        PhonemeResult phonemeResult,
        string? voiceOverride,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        var options = _options.CurrentValue;
        ValidateConfiguration(options);

        var voice = string.IsNullOrWhiteSpace(voiceOverride) ? options.DefaultVoice : voiceOverride;

        _logger.LogDebug(
            "Doubao TTS: synthesizing sentence of {Length} chars with voice '{Voice}'.",
            sentence.Length,
            voice
        );

        using var audioBuffer = new MemoryStream();
        DoubaoSentence? timing = null;

        var unreadable = false;

        try
        {
            await foreach (
                var evt in _client.SynthesizeAsync(sentence, voice, options, cancellationToken)
            )
            {
                if (evt.Audio is { Length: > 0 } chunk)
                {
                    audioBuffer.Write(chunk, 0, chunk.Length);
                }

                timing ??= evt.Sentence;
            }
        }
        catch (DoubaoApiException ex) when (ex.Code == NoReadableTextCode)
        {
            unreadable = true;
            _logger.LogDebug(
                "Doubao TTS reported no readable text for sentence '{Sentence}'; skipping.",
                Truncate(sentence)
            );
        }

        // yield break must sit outside the try/catch (C# forbids yielding from a try
        // block that has a catch clause).
        if (unreadable)
        {
            yield break;
        }

        if (audioBuffer.Length == 0)
        {
            throw new DoubaoApiException(
                $"Doubao TTS returned no audio for sentence: '{Truncate(sentence)}'"
            );
        }

        var (samples, sampleRate) = DecodeAudio(audioBuffer.ToArray(), options);
        var tokens = phonemeResult.Tokens;
        ApplyTokenTimings(tokens, samples.Length / (double)sampleRate, timing);

        yield return new AudioSegment(samples, sampleRate, tokens);
    }

    private static void ValidateConfiguration(DoubaoTtsOptions options)
    {
        var hasAuth = options.AuthMode switch
        {
            DoubaoTtsAuthMode.ApiKey => !string.IsNullOrWhiteSpace(options.ApiKey),
            DoubaoTtsAuthMode.AppIdAccessKey =>
                !string.IsNullOrWhiteSpace(options.AppId)
                && !string.IsNullOrWhiteSpace(options.AccessKey),
            _ => false,
        };

        if (!hasAuth)
        {
            throw new InvalidOperationException(
                "Doubao TTS is not configured for the selected authentication mode. "
                    + "Set Config:Tts:Doubao:ApiKey or the legacy AppId + AccessKey pair "
                    + "in appsettings.json."
            );
        }

        if (string.IsNullOrWhiteSpace(options.ResourceId))
        {
            throw new InvalidOperationException(
                "Doubao TTS requires a resource ID. "
                    + "Set Config:Tts:Doubao:ResourceId in appsettings.json."
            );
        }

        if (
            !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out _)
        )
        {
            throw new InvalidOperationException(
                $"Doubao TTS endpoint '{options.Endpoint}' is not a valid absolute URL."
            );
        }

        if (
            !options.Format.Equals("pcm", StringComparison.OrdinalIgnoreCase)
            && !options.Format.Equals("mp3", StringComparison.OrdinalIgnoreCase)
        )
        {
            throw new NotSupportedException(
                $"Doubao TTS format '{options.Format}' is not supported. Use 'pcm' or 'mp3'."
            );
        }
    }

    private static (Memory<float> Samples, int SampleRate) DecodeAudio(
        byte[] audio,
        DoubaoTtsOptions options
    )
    {
        if (options.Format.Equals("pcm", StringComparison.OrdinalIgnoreCase))
        {
            return (Pcm16ToFloat(audio), options.SampleRate);
        }

        // mp3: decode via NAudio, resample to the configured rate if needed.
        using var mp3 = new Mp3FileReader(new MemoryStream(audio));
        var source = mp3.ToSampleProvider();
        ISampleProvider provider = source;
        if (mp3.WaveFormat.SampleRate != options.SampleRate)
        {
            provider = new WdlResamplingSampleProvider(source, options.SampleRate);
        }

        var samples = new List<float>(Math.Max(1024, audio.Length * 4));
        var buffer = new float[8192];
        int read;
        while ((read = provider.Read(buffer, 0, buffer.Length)) > 0)
        {
            samples.AddRange(buffer.AsSpan(0, read).ToArray());
        }

        return (samples.ToArray().AsMemory(), options.SampleRate);
    }

    private static Memory<float> Pcm16ToFloat(byte[] pcm)
    {
        var frameCount = pcm.Length / 2;
        var samples = new float[frameCount];
        for (var i = 0; i < frameCount; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(pcm.AsSpan(i * 2, 2)) / 32768f;
        }

        return samples.AsMemory();
    }

    /// <summary>
    ///     Assigns token timings for subtitles/lip sync. Uses the API's word-level
    ///     timestamps when the word count matches the pipeline token count, otherwise
    ///     distributes the segment duration proportionally to token text length.
    /// </summary>
    private static void ApplyTokenTimings(
        IReadOnlyList<Token> tokens,
        double durationSeconds,
        DoubaoSentence? timing
    )
    {
        if (tokens.Count == 0)
        {
            return;
        }

        if (timing?.Words is { Count: > 0 } words && words.Count == tokens.Count)
        {
            for (var i = 0; i < tokens.Count; i++)
            {
                tokens[i].StartTs = words[i].StartTime;
                tokens[i].EndTs = words[i].EndTime;
            }

            return;
        }

        var totalWeight = tokens.Sum(t => Math.Max(1, t.Text.Length));
        var cursor = 0.0;
        foreach (var token in tokens)
        {
            var weight = Math.Max(1, token.Text.Length);
            token.StartTs = cursor;
            cursor += durationSeconds * weight / totalWeight;
            token.EndTs = cursor;
        }
    }

    private static string Truncate(string text, int maxLength = 60) =>
        text.Length <= maxLength ? text : text[..maxLength] + "...";

    /// <summary>
    ///     Stateless session for Doubao — each sentence is an independent HTTP
    ///     request, so <paramref name="isLastSegment" /> has nothing to flush.
    /// </summary>
    private sealed class DoubaoSynthesisSession(
        DoubaoSentenceSynthesizer owner,
        string? voiceOverride
    ) : ISynthesisSession
    {
        public IAsyncEnumerable<AudioSegment> SynthesizeAsync(
            string sentence,
            PhonemeResult phonemeResult,
            bool isLastSegment,
            CancellationToken cancellationToken = default
        ) => owner.SynthesizeCoreAsync(sentence, phonemeResult, voiceOverride, cancellationToken);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
