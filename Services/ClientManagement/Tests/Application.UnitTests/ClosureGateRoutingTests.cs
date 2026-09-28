using ClientManagement.Application.Common.Authorization;
using ClientManagement.Application.Common.Behaviours;
using ClientManagement.Application.Clients.Commands.AddClient;
using ClientManagement.Application.AssessmentDocument.Commands.GenerateAssessmentDocumentCommand;
using MediatR;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace Application.UnitTests;

public sealed class ClosureGateRoutingTests
{
    [Fact]
    public async Task Organisation_owned_commands_use_the_ordinary_activity_transaction_path()
    {
        var executor = new RecordingExecutor();
        var behavior = Behavior<AddClientCommand, int>(executor);

        await behavior.Handle(new AddClientCommand(), () => Task.FromResult(1), default);

        Assert.Equal(ActivityPath.Ordinary, executor.Path);
    }

    [Fact]
    public void Every_application_command_handler_has_an_explicit_activity_classification()
    {
        var discovered = typeof(AddClientCommand).Assembly.GetTypes()
            .Where(type => !type.IsAbstract)
            .SelectMany(type => type.GetInterfaces())
            .Where(contract => contract.IsGenericType
                && (contract.GetGenericTypeDefinition() == typeof(IRequestHandler<,>)
                    || contract.GetGenericTypeDefinition() == typeof(IRequestHandler<>)))
            .Select(contract => contract.GetGenericArguments()[0])
            .Where(request => request.Namespace?.Contains(".Commands.", StringComparison.Ordinal) is true)
            .Select(request => request.FullName!)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToArray();

        var classified = ClientRequestActivityClassifier.ClassifiedCommandTypeNames;
        var unclassified = discovered.Except(classified, StringComparer.Ordinal).ToArray();
        var stale = classified.Except(discovered, StringComparer.Ordinal).ToArray();
        Assert.True(unclassified.Length == 0, $"Unclassified handlers: {string.Join(", ", unclassified)}");
        Assert.True(stale.Length == 0, $"Stale classifications: {string.Join(", ", stale)}");
        Assert.Equal(ClientRequestActivity.OrdinaryOrganisationMutation,
            ClientRequestActivityClassifier.Classify(typeof(AddClientCommand)));
        Assert.Equal(ClientRequestActivity.Neutral,
            ClientRequestActivityClassifier.Classify(typeof(GenerateAssessmentDocumentCommand)));
    }

    [Fact]
    public async Task Reads_and_global_reference_commands_do_not_claim_an_ordinary_organisation_mutation()
    {
        var readExecutor = new RecordingExecutor();
        await Behavior<ReadQuery, Unit>(readExecutor).Handle(
            new ReadQuery(), () => Task.FromResult(Unit.Value), default);
        Assert.Equal(ActivityPath.Neutral, readExecutor.Path);

        var globalExecutor = new RecordingExecutor();
        await Behavior<GlobalReferenceCommand, Unit>(globalExecutor).Handle(
            new GlobalReferenceCommand(), () => Task.FromResult(Unit.Value), default);
        Assert.Equal(ActivityPath.Neutral, globalExecutor.Path);
    }

    private static AuthorizationBehaviour<TRequest, TResponse> Behavior<TRequest, TResponse>(RecordingExecutor executor)
        where TRequest : notnull
    {
        var organisation = Guid.NewGuid();
        var membership = Guid.NewGuid();
        var context = new TenantContextScope();
        var access = new FixedAccess(new AuthorizedTenantMembership(
            organisation,
            membership,
            ["Clients.Create", "Clients.EditAll", "Clients.ViewAll", "ReferenceData.View"],
            "life-02-test",
            DateTimeOffset.UtcNow));
        return new AuthorizationBehaviour<TRequest, TResponse>(
            new TenantOperation(access, new Identity(organisation), context, context), executor);
    }

    [RequiresTenantPermission("Clients.ViewAll")]
    private sealed record ReadQuery : IRequest<Unit>;

    [RequiresTenantPermission("ReferenceData.View")]
    private sealed record GlobalReferenceCommand : IRequest<Unit>;

    private sealed record Identity(Guid SelectedOrganisationId) : IOperationIdentity
    {
        public string SubjectId => "life-02-client-test";
    }

    private sealed class FixedAccess(AuthorizedTenantMembership membership) : ICurrentTenantAccess
    {
        public Task<TenantAccessDecision> ResolveAsync(string authenticatedSubjectId, Guid selectedOrganisationId,
            CancellationToken cancellationToken) => Task.FromResult(
                new TenantAccessDecision(TenantAccessOutcome.Authorized, membership));
    }

    private enum ActivityPath { None, Neutral, Ordinary, Lifecycle }

    private sealed class RecordingExecutor : ITenantTransactionExecutor
    {
        public ActivityPath Path { get; private set; }

        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken)
        {
            Path = ActivityPath.Neutral;
            return work(cancellationToken);
        }

        public async Task ExecuteAsync(Func<CancellationToken, Task> work, CancellationToken cancellationToken)
        {
            Path = ActivityPath.Neutral;
            await work(cancellationToken);
        }

        public Task<T> ExecuteOrdinaryAsync<T>(Func<CancellationToken, Task<T>> work,
            CancellationToken cancellationToken)
        {
            Path = ActivityPath.Ordinary;
            return work(cancellationToken);
        }

        public async Task ExecuteOrdinaryAsync(Func<CancellationToken, Task> work,
            CancellationToken cancellationToken)
        {
            Path = ActivityPath.Ordinary;
            await work(cancellationToken);
        }

        public Task<T> ExecuteLifecycleAsync<T>(Func<CancellationToken, Task<T>> work,
            CancellationToken cancellationToken)
        {
            Path = ActivityPath.Lifecycle;
            return work(cancellationToken);
        }

        public async Task ExecuteLifecycleAsync(Func<CancellationToken, Task> work,
            CancellationToken cancellationToken)
        {
            Path = ActivityPath.Lifecycle;
            await work(cancellationToken);
        }

        public Task<T> ExecuteOnceAsync<T>(Func<CancellationToken, Task<T>> work,
            CancellationToken cancellationToken) => work(cancellationToken);
    }
}
