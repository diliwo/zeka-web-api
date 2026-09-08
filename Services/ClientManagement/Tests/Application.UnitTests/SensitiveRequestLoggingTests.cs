using ClientManagement.Application.Clients.Commands.UpdateNativeLanguage;
using ClientManagement.Application.Clients.Queries.GetClients;
using ClientManagement.Application.Common.Behaviours;
using ClientManagement.Core.Interfaces;
using ClientManagement.Tests.Common;
using Microsoft.Extensions.Logging;

namespace Application.UnitTests;

public sealed class SensitiveRequestLoggingTests
{
    [Fact]
    public async Task Request_logging_does_not_capture_niss_or_search_payloads()
    {
        var niss = SyntheticClient.Niss();
        var update = new UpdateNativeLanguageCommand { Niss = niss, Language = "Français" };
        var search = new GetClientsBySearchTextQuery { SearchText = niss };
        var updateLog = new CapturingLogger<UpdateNativeLanguageCommand>();
        var searchLog = new CapturingLogger<GetClientsBySearchTextQuery>();

        await new LoggingBehaviour<UpdateNativeLanguageCommand>(updateLog, new TestUser()).Process(update, default);
        await new LoggingBehaviour<GetClientsBySearchTextQuery>(searchLog, new TestUser()).Process(search, default);

        updateLog.AssertSafe(niss);
        searchLog.AssertSafe(niss);
    }

    [Fact]
    public async Task Slow_request_logging_does_not_capture_search_payload()
    {
        var niss = SyntheticClient.Niss();
        var logger = new CapturingLogger<GetClientsBySearchTextQuery>();
        var behaviour = new PerformanceBehaviour<GetClientsBySearchTextQuery, int>(logger, new TestUser());
        await behaviour.Handle(new GetClientsBySearchTextQuery { SearchText = niss }, async () =>
        {
            await Task.Delay(550);
            return 1;
        }, default);
        logger.AssertSafe(niss);
    }

    [Fact]
    public async Task Exception_logging_does_not_capture_exception_message_or_request()
    {
        var niss = SyntheticClient.Niss();
        var logger = new CapturingLogger<UpdateNativeLanguageCommand>();
        var behaviour = new UnhandledExceptionBehaviour<UpdateNativeLanguageCommand, int>(logger);
        var failure = new InvalidOperationException(niss);
        var caught = await Assert.ThrowsAsync<InvalidOperationException>(() => behaviour.Handle(
            new UpdateNativeLanguageCommand { Niss = niss, Language = "Français" },
            () => Task.FromException<int>(failure), default));
        Assert.True(ReferenceEquals(failure, caught));
        logger.AssertSafe(niss);
    }

    private sealed class TestUser : ICurrentUserService
    {
        public string UserId => "synthetic-user";
        public string Username => "synthetic-user";
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        private readonly List<(object? State, Exception? Exception, string Message)> _entries = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => _entries.Add((state, exception, formatter(state, exception)));

        public void AssertSafe(string sensitiveValue)
        {
            Assert.NotEmpty(_entries);
            foreach (var entry in _entries)
            {
                Assert.Null(entry.Exception);
                Assert.False(entry.Message.Contains(sensitiveValue), "Formatted log must not contain sensitive input.");
                var properties = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(entry.State);
                Assert.All(properties, property =>
                {
                    Assert.True(property.Value is null or string or long, "Logs must not retain request objects.");
                    Assert.False(property.Value?.ToString()?.Contains(sensitiveValue) == true,
                        "Structured log fields must not contain sensitive input.");
                });
            }
        }
    }
}
