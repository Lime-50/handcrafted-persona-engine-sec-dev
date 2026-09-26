using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PersonaEngine.Lib.Core.Conversation.Abstractions.Configuration;
using PersonaEngine.Lib.Core.Conversation.Abstractions.Context;
using PersonaEngine.Lib.Core.Conversation.Abstractions.Session;
using PersonaEngine.Lib.Core.Conversation.Implementations.Context;
using PersonaEngine.Lib.Core.Conversation.Implementations.Session;
using Xunit;

namespace PersonaEngine.Lib.Tests.Core.Conversation;

public class ConversationOrchestratorTests
{
    [Fact]
    public async Task ClearAllContextsAsync_CancelsTurnAndClearsHistory_ForEveryActiveSession()
    {
        await using var harness = new OrchestratorHarness();
        var first = await harness.StartSessionAsync();
        var second = await harness.StartSessionAsync();

        await harness.Orchestrator.ClearAllContextsAsync();

        foreach (var (session, context) in new[] { first, second })
        {
            await session.Received(1).CancelAsync(Arg.Any<CancellationToken>());
            context.Received(1).ClearHistory();
        }
    }

    [Fact]
    public async Task ClearAllContextsAsync_NoActiveSessions_CompletesWithoutError()
    {
        await using var harness = new OrchestratorHarness();

        await harness.Orchestrator.ClearAllContextsAsync();

        Assert.Equal(0, harness.Orchestrator.ActiveSessionCount);
    }

    [Fact]
    public async Task ClearAllContextsAsync_OneSessionThrows_StillClearsTheOthers()
    {
        await using var harness = new OrchestratorHarness();
        var failing = await harness.StartSessionAsync();
        var healthy = await harness.StartSessionAsync();

        failing
            .Session.CancelAsync(Arg.Any<CancellationToken>())
            .Returns<ValueTask>(_ => throw new InvalidOperationException("boom"));

        await harness.Orchestrator.ClearAllContextsAsync();

        failing.Context.DidNotReceive().ClearHistory();
        healthy.Context.Received(1).ClearHistory();
    }

    private sealed class OrchestratorHarness : IAsyncDisposable
    {
        private readonly TaskCompletionSource _runCompletion = new();

        public OrchestratorHarness()
        {
            var factory = Substitute.For<IConversationSessionFactory>();
            factory
                .CreateSession(
                    Arg.Any<ConversationContext>(),
                    Arg.Any<ConversationOptions?>(),
                    Arg.Any<Guid?>()
                )
                .Returns(
                    _ =>
                    {
                        var context = Substitute.For<IConversationContext>();
                        var session = Substitute.For<IConversationSession>();
                        session.SessionId.Returns(Guid.NewGuid());
                        session.Context.Returns(context);
                        session
                            .RunAsync(Arg.Any<CancellationToken>())
                            .Returns(new ValueTask(_runCompletion.Task));
                        return session;
                    }
                );

            var monitor = Substitute.For<IOptionsMonitor<ConversationContextOptions>>();
            monitor.CurrentValue.Returns(new ConversationContextOptions());

            Orchestrator = new ConversationOrchestrator(
                NullLogger<ConversationOrchestrator>.Instance,
                factory,
                Options.Create(new ConversationOptions()),
                monitor
            );
        }

        public ConversationOrchestrator Orchestrator { get; }

        public async Task<(IConversationSession Session, IConversationContext Context)>
            StartSessionAsync()
        {
            var id = await Orchestrator.StartNewSessionAsync();
            var session = Orchestrator.GetSession(id);
            return (session, session.Context);
        }

        public async ValueTask DisposeAsync()
        {
            _runCompletion.TrySetResult();
            await Orchestrator.DisposeAsync();
        }
    }
}
