using System.Reflection;
using System.Text.Json.Serialization;
using AdminDeploymentDbContext = AdminAreaManagement.Infrastructure.Persistence.DeploymentDbContext;
using AuthDeploymentDbContext = AuthManager.Infrastructure.Persistence.AuthDbContext;
using ClientDeploymentDbContext = ClientManagement.Infrastructure.Persistence.DeploymentDbContext;

namespace Zeka.DbMigrate;

internal enum MigrationOperation { Plan, Apply }

internal sealed record CommandOptions(MigrationOperation Operation, ServiceDescriptor Service, string Target,
    Guid OperationId, string EvidenceRoot, string EvidenceFile, TimeSpan LockTimeout);

internal sealed record ServiceDescriptor(string Key, string LogicalDatabase, string MigratorRole, string OwnerRole,
    Type ContextType, Assembly MigrationAssembly)
{
    public static bool TryGet(string key, out ServiceDescriptor descriptor)
    {
        descriptor = key switch
        {
            "adminarea" => new("adminarea", "AdminAreaManagement", "zeka_adminarea_migrator",
                "zeka_adminarea_owner", typeof(AdminDeploymentDbContext), typeof(AdminDeploymentDbContext).Assembly),
            "auth" => new("auth", "AuthManagement", "zeka_auth_migrator",
                "zeka_auth_owner", typeof(AuthDeploymentDbContext), typeof(AuthDeploymentDbContext).Assembly),
            "client" => new("client", "ClientManagement", "zeka_client_migrator",
                "zeka_client_owner", typeof(ClientDeploymentDbContext), typeof(ClientDeploymentDbContext).Assembly),
            _ => null!
        };
        return descriptor is not null;
    }
}

internal sealed record PhaseResult([property: JsonPropertyName("status")] string Status);

internal sealed record RoleAssertions(
    [property: JsonPropertyName("expectedSessionUser")] string ExpectedSessionUser,
    [property: JsonPropertyName("observedSessionUser")] string? ObservedSessionUser,
    [property: JsonPropertyName("expectedCurrentUser")] string ExpectedCurrentUser,
    [property: JsonPropertyName("observedCurrentUser")] string? ObservedCurrentUser,
    [property: JsonPropertyName("restrictedAttributes")] bool RestrictedAttributes,
    [property: JsonPropertyName("matchingOwnerMembership")] bool MatchingOwnerMembership,
    [property: JsonPropertyName("ownerAssumption")] bool OwnerAssumption);

internal sealed record PhaseResults(
    [property: JsonPropertyName("migration")] PhaseResult Migration,
    [property: JsonPropertyName("bootstrapReconciliation")] PhaseResult Bootstrap,
    [property: JsonPropertyName("manifestVerification")] PhaseResult Manifest,
    [property: JsonPropertyName("runtimeReadiness")] PhaseResult RuntimeReadiness);

internal sealed record MigrationEvidence(
    [property: JsonPropertyName("schemaVersion")] string SchemaVersion,
    [property: JsonPropertyName("toolVersion")] string ToolVersion,
    [property: JsonPropertyName("sourceCommit")] string SourceCommit,
    [property: JsonPropertyName("operationId")] Guid OperationId,
    [property: JsonPropertyName("operation")] string Operation,
    [property: JsonPropertyName("service")] string Service,
    [property: JsonPropertyName("logicalDatabase")] string LogicalDatabase,
    [property: JsonPropertyName("requestedTarget")] string RequestedTarget,
    [property: JsonPropertyName("startingMigration")] string? StartingMigration,
    [property: JsonPropertyName("resolvedTarget")] string? ResolvedTarget,
    [property: JsonPropertyName("observedFinalMigration")] string? ObservedFinalMigration,
    [property: JsonPropertyName("classification")] string Classification,
    [property: JsonPropertyName("resultCode")] string ResultCode,
    [property: JsonPropertyName("advisoryLockAcquired")] bool AdvisoryLockAcquired,
    [property: JsonPropertyName("roles")] RoleAssertions Roles,
    [property: JsonPropertyName("phases")] PhaseResults Phases,
    [property: JsonPropertyName("startedAt")] DateTimeOffset StartedAt,
    [property: JsonPropertyName("endedAt")] DateTimeOffset EndedAt,
    [property: JsonPropertyName("caveats")] string[] Caveats);

internal static class ResultCodes
{
    public const int Success = 0;
    public const int InvalidInput = 20;
    public const int CredentialRejected = 21;
    public const int ConnectionRejected = 22;
    public const int LockUnavailable = 23;
    public const int IdentityRejected = 24;
    public const int HistoryRejected = 25;
    public const int DownTargetRejected = 26;
    public const int ManifestRejected = 27;
    public const int RequiresInspection = 28;
    public const int EvidenceRejected = 29;
    public const int InternalFailure = 30;
}
