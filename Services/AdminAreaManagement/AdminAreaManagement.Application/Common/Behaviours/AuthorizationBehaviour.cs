using System.Reflection;
using MediatR;
using AdminAreaManagement.Application.Common.Authorization;

namespace AdminAreaManagement.Application.Common.Behaviours;

public sealed class AuthorizationBehaviour<TRequest, TResponse>(TenantOperation operation)
    : IPipelineBehavior<TRequest, TResponse> where TRequest : notnull
{
    public async Task<TResponse> Handle(TRequest request, RequestHandlerDelegate<TResponse> next,
        CancellationToken cancellationToken)
    {
        var policy = typeof(TRequest).GetCustomAttribute<RequiresTenantPermissionAttribute>()
            ?? throw new TenantAccessException(AccessFailure.Denied);
        await operation.AuthorizeAsync(policy, cancellationToken);
        return await next();
    }
}
