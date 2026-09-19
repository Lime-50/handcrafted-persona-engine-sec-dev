using Microsoft.Extensions.Options;
using PersonaEngine.Lib.ASR.Transcriber.Doubao;
using PersonaEngine.Lib.Audio;
using PersonaEngine.Lib.Configuration;

namespace PersonaEngine.Lib.ASR.Transcriber;

/// <summary>
///     Chooses between the local Whisper transcriptor and the Doubao streaming
///     API transcriptor for each transcription session, based on the current
///     <see cref="AsrConfiguration.Provider" /> value.
/// </summary>
internal sealed class SelectingRealtimeTranscriptor : IRealtimeSpeechTranscriptor
{
    private readonly DoubaoRealtimeSpeechTranscriptor _doubaoTranscriptor;

    private readonly RealtimeTranscriptor _localTranscriptor;

    private readonly IOptionsMonitor<AsrConfiguration> _options;

    public SelectingRealtimeTranscriptor(
        IOptionsMonitor<AsrConfiguration> options,
        RealtimeTranscriptor localTranscriptor,
        DoubaoRealtimeSpeechTranscriptor doubaoTranscriptor
    )
    {
        _options = options;
        _localTranscriptor = localTranscriptor;
        _doubaoTranscriptor = doubaoTranscriptor;
    }

    public IAsyncEnumerable<IRealtimeRecognitionEvent> TranscribeAsync(
        IAwaitableAudioSource source,
        CancellationToken cancellationToken = default
    )
    {
        var provider = _options.CurrentValue.Provider;

        return provider == AsrProvider.Doubao
            ? _doubaoTranscriptor.TranscribeAsync(source, cancellationToken)
            : _localTranscriptor.TranscribeAsync(source, cancellationToken);
    }
}

