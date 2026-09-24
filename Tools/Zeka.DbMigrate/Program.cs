using System.Reflection;
using Zeka.DbMigrate;

var startedAt = DateTimeOffset.UtcNow;
if (!CommandLine.TryParse(args, out var options) || options is null)
{
    Console.Error.WriteLine("INVALID_INPUT");
    return ResultCodes.InvalidInput;
}

EvidenceOutput output;
try { output = EvidenceOutput.Create(options.EvidenceRoot, options.EvidenceFile); }
catch (EvidencePathException)
{
    Console.Error.WriteLine("EVIDENCE_PATH_REJECTED");
    return ResultCodes.EvidenceRejected;
}

var sourceCommit = Assembly.GetExecutingAssembly().GetCustomAttributes<AssemblyMetadataAttribute>()
    .SingleOrDefault(x => x.Key == "SourceCommit")?.Value ?? "unknown";
EngineResult result;
try
{
    var credential = await StandardInputCredentialSource.ReadAsync();
    result = await MigrationEngine.ExecuteAsync(options, credential, sourceCommit, startedAt);
}
catch (CredentialSourceException)
{
    result = Failure("CREDENTIAL_REJECTED", ResultCodes.CredentialRejected);
}

try { await output.PublishAsync(result.Evidence); }
catch
{
    Console.Error.WriteLine("EVIDENCE_PUBLISH_REJECTED");
    return ResultCodes.EvidenceRejected;
}
Console.WriteLine(result.Evidence.ResultCode);
return result.ExitCode;

EngineResult Failure(string code, int exitCode) => new(new MigrationEvidence(
    "zeka-db-migrate-result/v0.1.0", "0.1.0", sourceCommit, options.OperationId,
    options.Operation.ToString().ToLowerInvariant(), options.Service.Key, options.Service.LogicalDatabase,
    options.Target, null, null, null, "REJECTED", code, false,
    new RoleAssertions(options.Service.MigratorRole, null, options.Service.OwnerRole, null, false, false, false),
    new PhaseResults(new PhaseResult("not_started"), new PhaseResult("not_started"),
        new PhaseResult("not_started"), new PhaseResult("untested")), startedAt, DateTimeOffset.UtcNow,
    ["Bootstrap reconciliation and runtime readiness are separate host phases."]), exitCode);
