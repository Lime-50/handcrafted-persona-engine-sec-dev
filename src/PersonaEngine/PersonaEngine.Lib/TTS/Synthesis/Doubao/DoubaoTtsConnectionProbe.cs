using Microsoft.Extensions.Logging;
using PersonaEngine.Lib.Configuration;
using PersonaEngine.Lib.Health;

namespace PersonaEngine.Lib.TTS.Synthesis.Doubao;

/// <summary>
///     Lightweight reachability and authorization check for Doubao TTS. It sends
///     a short test phrase through the configured endpoint and succeeds as soon
///     as the service returns the first audio chunk.
/// </summary>
public interface IDoubaoTtsConnectionProbe
{
    Task<SubsystemStatus> ProbeAsync(
        DoubaoTtsOptions options,
        CancellationToken cancellationToken = default
    );
}

public sealed class DoubaoTtsConnectionProbe(
    DoubaoApiClient client,
    ILogger<DoubaoTtsConnectionProbe> logger
) : IDoubaoTtsConnectionProbe
{
    private const string ProbeText = "你好";

    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(15);

    public async Task<SubsystemStatus> ProbeAsync(
        DoubaoTtsOptions options,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        if (
            !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeHttps && endpoint.Scheme != Uri.UriSchemeHttp)
        )
        {
            return new SubsystemStatus(
                SubsystemHealth.Failed,
                "Invalid endpoint",
                "Doubao TTS expects an absolute http:// or https:// endpoint."
            );
        }

        if (string.IsNullOrWhiteSpace(options.DefaultVoice))
        {
            return new SubsystemStatus(
                SubsystemHealth.Failed,
                "Voice required",
                "Select or configure a default Doubao voice before testing."
            );
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        timeoutCts.CancelAfter(ProbeTimeout);

        try
        {
            var receivedAudio = false;

            await foreach (
                var evt in client
                    .SynthesizeAsync(
                        ProbeText,
                        options.DefaultVoice,
                        options,
                        timeoutCts.Token
                    )
            )
            {
                if (evt.Audio is { Length: > 0 })
                {
                    receivedAudio = true;
                    break;
                }
            }

            return receivedAudio
                ? new SubsystemStatus(
                    SubsystemHealth.Healthy,
                    "Connection successful",
                    "Doubao TTS accepted the credentials and returned audio."
                )
                : new SubsystemStatus(
                    SubsystemHealth.Failed,
                    "No audio",
                    "The TTS request completed without returning an audio chunk."
                );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SubsystemStatus(
                SubsystemHealth.Failed,
                "Timed out",
                $"No TTS audio within {ProbeTimeout.TotalSeconds:0} seconds."
            );
        }
        catch (DoubaoApiException ex)
        {
            return new SubsystemStatus(
                SubsystemHealth.Failed,
                "TTS API error",
                ex.Message
            );
        }
        catch (InvalidOperationException ex)
        {
            return new SubsystemStatus(
                SubsystemHealth.Failed,
                "Invalid configuration",
                ex.Message
            );
        }
        catch (HttpRequestException ex)
        {
            return new SubsystemStatus(
                SubsystemHealth.Failed,
                "HTTP error",
                ex.Message
            );
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Doubao TTS connection probe failed.");

            return new SubsystemStatus(
                SubsystemHealth.Failed,
                "Probe failed",
                ex.Message
            );
        }
    }
}
