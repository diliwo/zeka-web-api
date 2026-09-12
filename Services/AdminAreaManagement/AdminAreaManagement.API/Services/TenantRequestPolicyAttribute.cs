using System.Reflection;
using AdminAreaManagement.Application.Common.Authorization;

namespace AdminAreaManagement.API.Services;

// The HTTP description is derived from the authoritative Application request policy.
[AttributeUsage(AttributeTargets.Method)]
public sealed class TenantRequestPolicyAttribute(Type requestType) : Attribute
{
    public Type RequestType { get; } = requestType;
    public RequiresTenantPermissionAttribute Policy => RequestType.GetCustomAttribute<RequiresTenantPermissionAttribute>()
        ?? throw new InvalidOperationException("HTTP request has no Application authorization policy.");
}
