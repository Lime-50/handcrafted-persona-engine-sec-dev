using System.Net.Http;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using PersonaEngine.Lib.Configuration;
using PersonaEngine.Lib.Health;
using PersonaEngine.Lib.TTS.Synthesis.Doubao;
using Xunit;

namespace PersonaEngine.Lib.Tests.TTS.Doubao;

public sealed class DoubaoTtsConnectionProbeTests
{
    [Fact]
    public async Task ProbeAsync_InvalidEndpoint_ReturnsFailed()
    {
        var probe = CreateProbe();

        var status = await probe.ProbeAsync(
            new DoubaoTtsOptions { Endpoint = "not-a-url" }
        );

        status.Health.Should().Be(SubsystemHealth.Failed);
        status.Label.Should().Be("Invalid endpoint");
    }

    [Fact]
    public async Task ProbeAsync_MissingVoice_ReturnsFailed()
    {
        var probe = CreateProbe();

        var status = await probe.ProbeAsync(
            new DoubaoTtsOptions { DefaultVoice = string.Empty }
        );

        status.Health.Should().Be(SubsystemHealth.Failed);
        status.Label.Should().Be("Voice required");
    }

    [Fact]
    public async Task ProbeAsync_MissingApiKey_ReturnsInvalidConfiguration()
    {
        var probe = CreateProbe();

        var status = await probe.ProbeAsync(
            new DoubaoTtsOptions
            {
                AuthMode = DoubaoTtsAuthMode.ApiKey,
                ApiKey = string.Empty,
            }
        );

        status.Health.Should().Be(SubsystemHealth.Failed);
        status.Label.Should().Be("Invalid configuration");
        status.Detail.Should().Contain("API key");
    }

    private static DoubaoTtsConnectionProbe CreateProbe()
    {
        var client = new DoubaoApiClient(
            Substitute.For<IHttpClientFactory>(),
            Substitute.For<IOptionsMonitor<DoubaoTtsOptions>>(),
            Substitute.For<ILogger<DoubaoApiClient>>()
        );

        return new DoubaoTtsConnectionProbe(
            client,
            Substitute.For<ILogger<DoubaoTtsConnectionProbe>>()
        );
    }
}
