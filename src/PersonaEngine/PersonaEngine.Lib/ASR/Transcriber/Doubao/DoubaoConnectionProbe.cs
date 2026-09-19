using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PersonaEngine.Lib.Configuration;
using PersonaEngine.Lib.Health;

namespace PersonaEngine.Lib.ASR.Transcriber.Doubao;

/// <summary>
///     Lightweight reachability check for the Doubao streaming ASR WebSocket
///     endpoint. The probe opens a connection, sends the initial full-client
///     request and an empty final audio frame, then waits briefly for either an
///     error frame, an ASR response, or a normal WebSocket close.
/// </summary>
public interface IDoubaoConnectionProbe
{
    Task<SubsystemStatus> ProbeAsync(
        DoubaoAsrOptions options,
        CancellationToken cancellationToken = default
    );
}

public sealed class DoubaoConnectionProbe(ILogger<DoubaoConnectionProbe> logger)
    : IDoubaoConnectionProbe
{
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan HttpDiagnosticTimeout = TimeSpan.FromSeconds(3);

    private static readonly TimeSpan ResourceProbeTimeout = TimeSpan.FromSeconds(3);

    private static readonly string[] OfficialResourceIds =
    [
        "volc.bigasr.sauc.duration",
        "volc.bigasr.sauc.concurrent",
        "volc.seedasr.sauc.duration",
        "volc.seedasr.sauc.concurrent",
    ];

    public async Task<SubsystemStatus> ProbeAsync(
        DoubaoAsrOptions options,
        CancellationToken cancellationToken = default
    )
    {
        ArgumentNullException.ThrowIfNull(options);

        if (
            !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            || (endpoint.Scheme != Uri.UriSchemeWs && endpoint.Scheme != Uri.UriSchemeWss)
        )
        {
            return new SubsystemStatus(
                SubsystemHealth.Failed,
                "Invalid endpoint",
                "Doubao ASR expects a ws:// or wss:// WebSocket URL."
            );
        }

        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        timeoutCts.CancelAfter(ProbeTimeout);

        IReadOnlyDictionary<string, string>? requestHeaders = null;

        try
        {
            requestHeaders = DoubaoProtocol.BuildRequestHeaders(options);
            DoubaoProtocol.ConfigureRequestHeaders(socket, requestHeaders);
            await socket.ConnectAsync(endpoint, timeoutCts.Token);

            return new SubsystemStatus(
                SubsystemHealth.Healthy,
                "Connection successful",
                "The Doubao WebSocket endpoint accepted the credentials and "
                    + "completed the handshake."
            );
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new SubsystemStatus(
                SubsystemHealth.Failed,
                "Timed out",
                $"The WebSocket handshake did not complete within "
                    + $"{ProbeTimeout.TotalSeconds:0} seconds."
            );
        }
        catch (WebSocketException ex)
        {
            string detail;
            try
            {
                detail = await DescribeHandshakeFailureAsync(
                    endpoint,
                    requestHeaders,
                    socket,
                    ex,
                    cancellationToken
                );

                if (
                    socket.HttpStatusCode == HttpStatusCode.Forbidden
                    && !cancellationToken.IsCancellationRequested
                )
                {
                    var workingResourceId = await TryFindWorkingResourceIdAsync(
                        options,
                        cancellationToken
                    );
                    if (!string.IsNullOrWhiteSpace(workingResourceId))
                    {
                        detail +=
                            $" The server accepted Resource ID '{workingResourceId}'. "
                            + "Update the Resource ID field to that value and test again.";
                    }
                }
            }
            catch (Exception diagnosticException)
            {
                logger.LogWarning(
                    diagnosticException,
                    "Doubao ASR handshake diagnostics failed."
                );
                detail = ex.Message;
            }

            return new SubsystemStatus(
                SubsystemHealth.Failed,
                "Connection failed",
                detail
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
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Doubao ASR connection probe failed.");

            return new SubsystemStatus(
                SubsystemHealth.Failed,
                "Probe failed",
                ex.Message
            );
        }
    }

    private static async Task<string> DescribeHandshakeFailureAsync(
        Uri endpoint,
        IReadOnlyDictionary<string, string>? requestHeaders,
        ClientWebSocket socket,
        WebSocketException exception,
        CancellationToken cancellationToken
    )
    {
        var statusCode = socket.HttpStatusCode is { } status
            ? (int)status
            : (int?)null;

        string message;
        if (statusCode is 401 or 403)
        {
            message =
                "The server rejected the WebSocket handshake. Check that the "
                + "authentication mode matches the credentials, that the API key or "
                + "App ID + Access Token is valid, and that the account has the "
                + "configured Resource ID enabled.";
        }
        else if (statusCode == 429)
        {
            message =
                "The server rate-limited the WebSocket connection. Wait briefly and "
                + "try again.";
        }
        else
        {
            return exception.Message;
        }

        message = $"HTTP {statusCode}: {message} ({exception.Message})";

        var apiStatus = TryGetResponseHeader(socket, "X-Api-Status-Code");
        var apiMessage = TryGetResponseHeader(socket, "X-Api-Message");
        if (!string.IsNullOrWhiteSpace(apiStatus) || !string.IsNullOrWhiteSpace(apiMessage))
        {
            message +=
                $" Server status: {apiStatus ?? "unknown"} {apiMessage ?? string.Empty}.";
        }

        var logId = TryGetResponseHeader(socket, "X-Tt-Logid")
            ?? TryGetResponseHeader(socket, "X-Request-Id");
        if (!string.IsNullOrWhiteSpace(logId))
        {
            message += $" Server log ID: {logId}.";
        }

        if (
            statusCode is 401 or 403
            && requestHeaders is not null
            && !cancellationToken.IsCancellationRequested
        )
        {
            var serverError = await TryGetHttpHandshakeErrorAsync(
                endpoint,
                requestHeaders,
                cancellationToken
            );
            if (!string.IsNullOrWhiteSpace(serverError))
            {
                message += $" Server response: {serverError}";
                if (!message.EndsWith(".", StringComparison.Ordinal))
                {
                    message += ".";
                }
            }
        }

        return message;
    }

    private static async Task<string?> TryFindWorkingResourceIdAsync(
        DoubaoAsrOptions options,
        CancellationToken cancellationToken
    )
    {
        foreach (var resourceId in OfficialResourceIds)
        {
            if (
                string.Equals(
                    resourceId,
                    options.ResourceId?.Trim(),
                    StringComparison.OrdinalIgnoreCase
                )
            )
            {
                continue;
            }

            var candidate = options with { ResourceId = resourceId };
            if (await CanConnectWithResourceIdAsync(candidate, cancellationToken))
            {
                return resourceId;
            }
        }

        return null;
    }

    private static async Task<bool> CanConnectWithResourceIdAsync(
        DoubaoAsrOptions options,
        CancellationToken cancellationToken
    )
    {
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        timeoutCts.CancelAfter(ResourceProbeTimeout);

        try
        {
            if (
                !Uri.TryCreate(options.Endpoint, UriKind.Absolute, out var endpoint)
            )
            {
                return false;
            }

            DoubaoProtocol.ConfigureRequestHeaders(socket, options);
            await socket.ConnectAsync(endpoint, timeoutCts.Token);

            return socket.State == WebSocketState.Open;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static async Task<string?> TryGetHttpHandshakeErrorAsync(
        Uri endpoint,
        IReadOnlyDictionary<string, string> requestHeaders,
        CancellationToken cancellationToken
    )
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken
        );
        timeoutCts.CancelAfter(HttpDiagnosticTimeout);

        try
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
            };

            request.Headers.TryAddWithoutValidation("Connection", "Upgrade");
            request.Headers.TryAddWithoutValidation("Upgrade", "websocket");
            request.Headers.TryAddWithoutValidation("Sec-WebSocket-Version", "13");
            request.Headers.TryAddWithoutValidation(
                "Sec-WebSocket-Key",
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))
            );

            foreach (var (name, value) in requestHeaders)
            {
                request.Headers.TryAddWithoutValidation(name, value);
            }

            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutCts.Token
            );
            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token);

            return ExtractServerError(body);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? ExtractServerError(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        var trimmed = body.Trim();

        try
        {
            using var document = JsonDocument.Parse(trimmed);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                if (
                    document.RootElement.TryGetProperty("error", out var error)
                    && error.ValueKind == JsonValueKind.String
                )
                {
                    return error.GetString();
                }

                if (
                    document.RootElement.TryGetProperty("message", out var message)
                    && message.ValueKind == JsonValueKind.String
                )
                {
                    return message.GetString();
                }
            }
        }
        catch (JsonException)
        {
            // Fall through to the raw response body.
        }

        return trimmed.Length <= 500 ? trimmed : trimmed[..500];
    }

    private static string? TryGetResponseHeader(
        ClientWebSocket socket,
        string name
    )
    {
        if (
            socket.HttpResponseHeaders is { } headers
            && headers.TryGetValue(name, out var values)
        )
        {
            return string.Join(", ", values);
        }

        return null;
    }

}
