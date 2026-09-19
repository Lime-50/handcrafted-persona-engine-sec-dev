using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PersonaEngine.Lib.Audio;
using PersonaEngine.Lib.Configuration;

namespace PersonaEngine.Lib.ASR.Transcriber.Doubao;

/// <summary>
///     Streaming ASR implementation backed by the Volcengine/Doubao big-model
///     v3 WebSocket API. It preserves the existing
///     <see cref="IRealtimeSpeechTranscriptor" /> event contract so the
///     conversation pipeline can switch engines without changing its consumers.
/// </summary>
internal sealed class DoubaoRealtimeSpeechTranscriptor : IRealtimeSpeechTranscriptor
{
    private readonly ILogger<DoubaoRealtimeSpeechTranscriptor> _logger;

    private readonly IOptionsMonitor<AsrConfiguration> _options;

    public DoubaoRealtimeSpeechTranscriptor(
        IOptionsMonitor<AsrConfiguration> options,
        ILogger<DoubaoRealtimeSpeechTranscriptor> logger
    )
    {
        _options = options;
        _logger = logger;
    }

    public async IAsyncEnumerable<IRealtimeRecognitionEvent> TranscribeAsync(
        IAwaitableAudioSource source,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(source);

        var asr = _options.CurrentValue;
        var options = asr.Doubao;
        var sessionId = Guid.NewGuid().ToString();

        await source.WaitForInitializationAsync(cancellationToken);

        _logger.LogInformation(
            "Starting Doubao streaming ASR session {SessionId} at {Endpoint}",
            sessionId,
            options.Endpoint
        );

        yield return new RealtimeSessionStarted(sessionId);

        using var socket = new ClientWebSocket();
        DoubaoProtocol.ConfigureRequestHeaders(socket, options);

        var endpoint = new Uri(options.Endpoint);
        await socket.ConnectAsync(endpoint, cancellationToken);

        var requestJson = DoubaoProtocol.BuildFullClientRequest(
            options,
            DoubaoProtocol.ResolveAudioLanguage(options),
            source.SampleRate,
            source.BitsPerSample,
            source.ChannelCount
        );

        var requestFrame = DoubaoProtocol.CreateFullClientFrame(requestJson);
        await socket.SendAsync(
            requestFrame,
            WebSocketMessageType.Binary,
            true,
            cancellationToken
        );

        var receiveChannel = Channel.CreateUnbounded<DoubaoReceivedMessage>();
        using var receiveCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        var receiveTask = ReceiveLoopAsync(
            socket,
            receiveChannel.Writer,
            receiveCts.Token
        );

        var bytesPerFrame = Math.Max(
            1,
            source.BitsPerSample * source.ChannelCount / 8
        );
        var packetFrames = Math.Max(
            1,
            (int)(source.SampleRate * options.PacketDurationMs / 1000d)
        );
        long sentFrames = 0;

        try
        {
            while (!source.IsFlushed)
            {
                var sentDuration = TimeSpan.FromSeconds(
                    sentFrames / (double)source.SampleRate
                );
                var targetDuration =
                    sentDuration + TimeSpan.FromMilliseconds(options.PacketDurationMs);

                if (source.Duration >= targetDuration)
                {
                    var availableFrames = source.FramesCount;
                    if (availableFrames > sentFrames)
                    {
                        var frameCount = (int)
                            Math.Min(packetFrames, availableFrames - sentFrames);
                        var pcm = await source.GetFramesAsync(
                            sentFrames,
                            frameCount,
                            cancellationToken
                        );

                        if (pcm.Length > 0)
                        {
                            var pcmBytes = pcm.ToArray();
                            var audioFrame = DoubaoProtocol.CreateAudioFrame(
                                pcmBytes,
                                isLast: false
                            );

                            await socket.SendAsync(
                                audioFrame,
                                WebSocketMessageType.Binary,
                                true,
                                cancellationToken
                            );

                            sentFrames += pcmBytes.Length / bytesPerFrame;
                        }
                    }
                }
                else
                {
                    await source.WaitForNewSamplesAsync(
                        targetDuration,
                        cancellationToken
                    );
                }

                await foreach (
                    var @event in DrainReceiveChannelAsync(
                        receiveChannel.Reader,
                        sessionId,
                        cancellationToken
                    )
                )
                {
                    yield return @event;
                }
            }

            var finalAudioFrame = DoubaoProtocol.CreateAudioFrame(
                ReadOnlySpan<byte>.Empty,
                isLast: true
            );
            await socket.SendAsync(
                finalAudioFrame,
                WebSocketMessageType.Binary,
                true,
                cancellationToken
            );

            await receiveTask.WaitAsync(cancellationToken);
            receiveChannel.Writer.TryComplete();

            await foreach (
                var @event in DrainReceiveChannelAsync(
                    receiveChannel.Reader,
                    sessionId,
                    cancellationToken
                )
            )
            {
                yield return @event;
            }

            yield return new RealtimeSessionStopped(sessionId);
        }
        finally
        {
            receiveCts.Cancel();
            receiveChannel.Writer.TryComplete();

            try
            {
                await receiveTask.WaitAsync(CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                // Expected when the outer token or socket closure cancels the loop.
            }
        }
    }

    private async Task ReceiveLoopAsync(
        ClientWebSocket socket,
        ChannelWriter<DoubaoReceivedMessage> writer,
        CancellationToken cancellationToken
    )
    {
        try
        {
            var buffer = new byte[64 * 1024];
            using var stream = new MemoryStream();

            while (
                socket.State == WebSocketState.Open
                && !cancellationToken.IsCancellationRequested
            )
            {
                var result = await socket.ReceiveAsync(
                    buffer.AsMemory(),
                    cancellationToken
                );

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }

                if (result.MessageType != WebSocketMessageType.Binary)
                {
                    continue;
                }

                stream.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage)
                {
                    continue;
                }

                var data = stream.ToArray();
                stream.SetLength(0);

                ProcessBinaryFrame(data, writer);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation from the outer enumerable.
        }
        catch (Exception ex) when (
            ex is WebSocketException or DoubaoAsrException
        )
        {
            writer.TryWrite(new DoubaoReceivedMessage(null, ex));
        }
        finally
        {
            writer.TryComplete();
        }
    }

    private static void ProcessBinaryFrame(
        byte[] data,
        ChannelWriter<DoubaoReceivedMessage> writer
    )
    {
        var offset = 0;
        while (offset + DoubaoProtocol.HeaderSize <= data.Length)
        {
            if (
                !DoubaoProtocol.TryReadFrameHeader(
                    data.AsSpan(offset),
                    out var header
                )
            )
            {
                break;
            }

            if (offset + DoubaoProtocol.HeaderSize + DoubaoProtocol.LengthSize > data.Length)
            {
                break;
            }

            var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(
                data.AsSpan(
                    offset + DoubaoProtocol.HeaderSize,
                    DoubaoProtocol.LengthSize
                )
            );

            if (payloadLength > int.MaxValue)
            {
                break;
            }

            var payloadStart = offset + DoubaoProtocol.HeaderSize + DoubaoProtocol.LengthSize;
            if (payloadStart + (int)payloadLength > data.Length)
            {
                break;
            }

            var payload = data.AsSpan(payloadStart, (int)payloadLength);
            if (header.MessageType == DoubaoProtocol.MessageTypeError)
            {
                var message =
                    DoubaoProtocol.ParseErrorMessage(payload)
                    ?? "Unknown Doubao streaming ASR error.";
                throw new DoubaoAsrException(message);
            }

            if (header.MessageType != DoubaoProtocol.MessageTypeFullServerResponse)
            {
                offset = payloadStart + (int)payloadLength;

                continue;
            }

            var response = DoubaoProtocol.ParseServerResponse(payload);
            if (response is not null)
            {
                writer.TryWrite(new DoubaoReceivedMessage(response, null));
            }

            offset = payloadStart + (int)payloadLength;
        }
    }

    private async IAsyncEnumerable<IRealtimeRecognitionEvent> DrainReceiveChannelAsync(
        ChannelReader<DoubaoReceivedMessage> reader,
        string sessionId,
        [EnumeratorCancellation] CancellationToken cancellationToken
    )
    {
        while (reader.TryRead(out var message))
        {
            if (message.Error is not null)
            {
                throw message.Error;
            }

            if (message.Response is null)
            {
                continue;
            }

            foreach (var @event in ToRealtimeEvents(message.Response, sessionId))
            {
                yield return @event;
            }
        }

        await Task.CompletedTask;
    }

    private static IEnumerable<IRealtimeRecognitionEvent> ToRealtimeEvents(
        DoubaoProtocol.DoubaoServerResponse response,
        string sessionId
    )
    {
        if (response.Utterances.Count > 0)
        {
            foreach (var utterance in response.Utterances)
            {
                if (string.IsNullOrWhiteSpace(utterance.Text))
                {
                    continue;
                }

                var segment = new TranscriptSegment
                {
                    Metadata = new Dictionary<string, string>(),
                    Text = utterance.Text,
                    StartTime = utterance.StartTime,
                    Duration = utterance.Duration,
                };

                yield return utterance.Definite
                    ? new RealtimeSegmentRecognized(
                        segment,
                        sessionId,
                        TimeSpan.Zero
                    )
                    : new RealtimeSegmentRecognizing(
                        segment,
                        sessionId,
                        TimeSpan.Zero
                    );
            }

            yield break;
        }

        if (string.IsNullOrWhiteSpace(response.Text))
        {
            yield break;
        }

        var textSegment = new TranscriptSegment
        {
            Metadata = new Dictionary<string, string>(),
            Text = response.Text,
            StartTime = TimeSpan.Zero,
            Duration = TimeSpan.Zero,
        };

        yield return new RealtimeSegmentRecognizing(
            textSegment,
            sessionId,
            TimeSpan.Zero
        );
    }

    private sealed record DoubaoReceivedMessage(
        DoubaoProtocol.DoubaoServerResponse? Response,
        Exception? Error
    );

    private sealed class DoubaoAsrException(string message)
        : InvalidOperationException(message);
}
