namespace PersonaEngine.Lib.TTS.Synthesis.Doubao;

/// <summary>
///     Raised when the Doubao speech synthesis API returns a non-success code
///     or an otherwise unusable response.
/// </summary>
public sealed class DoubaoApiException : Exception
{
    public DoubaoApiException(int code, string message)
        : base($"Doubao TTS API error ({code}): {message}")
    {
        Code = code;
    }

    public DoubaoApiException(string message)
        : base(message)
    {
    }

    /// <summary>Volcengine API result code (0 = partial success, 20000000 = stream end).</summary>
    public int Code { get; }
}
