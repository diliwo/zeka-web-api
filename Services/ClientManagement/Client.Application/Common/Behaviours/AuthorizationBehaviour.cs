using System.Reflection;
using MediatR;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Application.ActionWrites;

namespace ClientManagement.Application.Common.Behaviours;

public sealed class AuthorizationBehaviour<TRequest, TResponse>(TenantOperation operation,
    ITenantTransactionExecutor transactions, PostCommitNotifications notifications,
    IActionWriteStore actionWrites)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var policy = typeof(TRequest).GetCustomAttribute<RequiresTenantPermissionAttribute>()
            ?? throw new TenantAccessException(AccessFailure.Denied);
        await operation.AuthorizeAsync(policy, cancellationToken);
        var ordinaryMutation = ClientRequestActivityClassifier.Classify(typeof(TRequest))
            == ClientRequestActivity.OrdinaryOrganisationMutation;
        TResponse result;
        if (ClientRequestActivityClassifier.RequiresNonReplayingOrdinary(typeof(TRequest)))
        {
            try
            {
                result = await transactions.ExecuteOrdinaryOnceAsync(_ => next(), cancellationToken);
            }
            catch (TenantCommitIndeterminateException)
            {
                // Fresh, guarded read only. Never replay the mutation or a remote authority decision.
                object? reconciled = request switch
                {
                    RecordInteractionCommand command => (object?)await transactions.ExecuteOrdinaryOnceAsync(
                        token => actionWrites.ReconcileAsync(command, operation.MembershipId, token), cancellationToken),
                    ActionChangeCommand command => (object?)await transactions.ExecuteOrdinaryOnceAsync(
                        token => actionWrites.ReconcileAsync(command, operation.MembershipId, token), cancellationToken),
                    _ => null
                };
                if (reconciled is not TResponse typed) throw;
                result = typed;
            }
        }
        else if (ordinaryMutation)
            result = await transactions.ExecuteOrdinaryAsync(_ => next(), cancellationToken);
        else
            result = await transactions.ExecuteAsync(_ => next(), cancellationToken);
        await notifications.PublishAsync(cancellationToken);
        return result;
    }
}
