using System.Diagnostics;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Core.Entities;
using ClientManagement.Infrastructure;
using ClientManagement.Infrastructure.Authorization;
using ClientManagement.Infrastructure.Persistence;
using ClientManagement.Tests.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Zeka.LocalB2Prerequisites;

/// <summary>Owns Client-side synthetic rows only; SocialWorker rows must arrive through the real consumer.</summary>
[Trait("Issue", "97")]
[Trait("Evidence", "LocalB2Prerequisites")]
public sealed class B2LocalPrerequisiteFixtureTests
{
    [Fact] public Task VerifyCatalog() => Run(nameof(VerifyCatalog));
    [Fact] public Task Initialize() => Run(nameof(Initialize));
    [Fact] public Task Readback() => Run(nameof(Readback));
    [Fact] public Task Teardown() => Run(nameof(Teardown));

    [Fact]
    public void No_operation_is_inert_without_manifest_or_credentials()
    {
        Assert.False(B2FixtureManifest.Selected("Initialize", null));
        Assert.False(B2FixtureManifest.Selected("Initialize", ""));
        Assert.False(B2FixtureManifest.Selected("Initialize", "Readback"));
        Assert.Throws<InvalidOperationException>(() => B2FixtureManifest.Selected("Initialize", "unapproved"));
    }

    [Fact]
    public void Exact_Development_target_is_admitted_without_io()
    {
        var document = JsonNode.Parse("""
            {"schemaVersion":1,"environment":"Development","exclusiveTarget":true,
             "sourceBaseline":"198a66015a5a90e2936f3caa263774d74011d1ec",
             "sourceCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
             "postgres":{"host":"127.0.0.1","port":5432,"authDatabase":"zekaAuthdb",
              "adminDatabase":"zekaAdminAreadb","clientDatabase":"zekaClientdb",
              "containerId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}
            """)!.AsObject();
        B2FixtureManifest.ValidateTarget(document, new string('a', 40));
    }


    [Theory]
    [InlineData("environment")] [InlineData("exclusiveTarget")] [InlineData("sourceCommit")]
    [InlineData("host")] [InlineData("adminDatabase")] [InlineData("clientDatabase")]
    public void Target_or_source_mismatch_fails_before_io(string field)
    {
        var document = JsonNode.Parse("""
            {"schemaVersion":1,"environment":"Development","exclusiveTarget":true,
             "sourceBaseline":"198a66015a5a90e2936f3caa263774d74011d1ec",
             "sourceCommit":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
             "postgres":{"host":"127.0.0.1","port":5432,"authDatabase":"zekaAuthdb",
              "adminDatabase":"zekaAdminAreadb","clientDatabase":"zekaClientdb",
              "containerId":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}}
            """)!.AsObject();
        if (field == "exclusiveTarget") document[field] = false;
        else if (field is "host" or "adminDatabase" or "clientDatabase") document["postgres"]![field] = "wrong";
        else document[field] = "wrong";
        Assert.Throws<InvalidOperationException>(() => B2FixtureManifest.ValidateTarget(document, new string('a', 40)));
    }

    private static async Task Run(string operation)
    {
        if (!B2FixtureManifest.Selected(operation, Environment.GetEnvironmentVariable("ZEKA_B2_OPERATION"))) return;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var manifest = B2FixtureManifest.Load("client");
        var configuration = manifest.Configuration("client", "ZEKA_B2_CLIENT_RUNTIME_CONNECTION");
        try
        {
            await manifest.VerifyRuntimeAsync(configuration.GetConnectionString("ClientApiConnection")!,
                "zeka_client_runtime", "zekaClientdb", deadline.Token);
            if (operation == "Initialize") await InitializeOwnedAsync(manifest, deadline.Token);
            else if (operation == "Readback") await ReadOwnedAsync(manifest, deadline.Token);
            else if (operation == "Teardown") await TeardownOwnedAsync(manifest, deadline.Token);
            else
            {
                await using var connection = new NpgsqlConnection(configuration.GetConnectionString("ClientApiConnection"));
                await connection.OpenAsync(deadline.Token);
                await using var command = new NpgsqlCommand("""
                    select to_regclass('"Clients"') is not null
                      and to_regclass('"SocialCases"') is not null
                      and to_regclass('"SocialWorkers"') is not null
                    """, connection) { CommandTimeout = 10 };
                Assert.True(await command.ExecuteScalarAsync(deadline.Token) is true, "Required Client schema is absent.");
            }
        }
        catch (Exception failure)
        {
            // Raw provider/configuration/token-bearing exceptions must never enter TRX or shared evidence.
            throw new InvalidOperationException($"Client B2 {operation} failed ({failure.GetType().Name}); retain owned resources for bounded review.");
        }
    }

    private static ServiceProvider Provider(B2FixtureManifest manifest, Guid organisation)
    {
        var configuration = manifest.Configuration("client", "ZEKA_B2_CLIENT_RUNTIME_CONNECTION");
        var session = manifest.Session(organisation);
        var services = new ServiceCollection();
        services.AddLogging(logging => logging.ClearProviders());
        services.AddSingleton<IConfiguration>(configuration);
        services.AddSingleton<IOperationIdentity>(session);
        services.AddSingleton<ITenantAccessCredential>(session);
        services.AddScoped<TenantOperation>();
        services.AddInfrastructure(configuration);
        services.AddHttpClient<ICurrentTenantAccess, CurrentTenantAccessClient>()
            .ConfigurePrimaryHttpMessageHandler(manifest.TrustedHandler);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
    }

    private static async Task<T> AuthorizedAsync<T>(B2FixtureManifest manifest, Guid organisation,
        string permission, Func<ApplicationDbContext, CancellationToken, Task<T>> work, CancellationToken token)
    {
        await using var provider = Provider(manifest, organisation);
        await using var scope = provider.CreateAsyncScope();
        var operation = scope.ServiceProvider.GetRequiredService<TenantOperation>();
        await operation.AuthorizeAsync(new RequiresTenantPermissionAttribute(permission), token);
        Assert.Equal(manifest.Session(organisation).ExpectedMembershipId, operation.MembershipId);
        var database = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await scope.ServiceProvider.GetRequiredService<ITenantTransactionExecutor>()
            .ExecuteOrdinaryOnceAsync(ct => work(database, ct), token);
    }

    private static async Task<Dictionary<string, int>> WaitForGenuineProjectionsAsync(B2FixtureManifest manifest,
        Guid organisation, string[] members, CancellationToken token)
    {
        var wait = Stopwatch.StartNew();
        while (wait.Elapsed < TimeSpan.FromSeconds(40))
        {
            token.ThrowIfCancellationRequested();
            var found = await AuthorizedAsync(manifest, organisation, "Clients.EditAll", async (database, ct) =>
            {
                var result = new Dictionary<string, int>(StringComparer.Ordinal);
                foreach (var logical in members)
                {
                    var membership = manifest.Membership(logical);
                    var worker = await database.SocialWorkers.SingleOrDefaultAsync(
                        x => x.OrganisationMembershipId == membership, ct);
                    if (worker is null) continue;
                    if (worker.OrganisationId != organisation || worker.UserName != manifest.Marker(logical)
                        || worker.Softdelete || worker.ProjectionVersion <= 0 || worker.LastProjectionEventId == Guid.Empty)
                        throw new InvalidOperationException("B2 genuine projection identity mismatch.");
                    result.Add(logical, worker.Id);
                }
                return result;
            }, token);
            if (found.Count == members.Length) return found;
            // Bounded readiness polling only. This never starts a consumer, publishes an event or synthesizes a projection.
            await Task.Delay(TimeSpan.FromMilliseconds(200), token);
        }
        throw new InvalidOperationException("The actual Staff outbox/Rabbit consumer has not produced the required Client projections.");
    }

    private static async Task InitializeOwnedAsync(B2FixtureManifest manifest, CancellationToken token)
    {
        if (manifest.State.Count != 0) throw new InvalidOperationException("B2 Client initialization is single-execution only.");
        var workersA = await WaitForGenuineProjectionsAsync(manifest, manifest.TenantA,
            ["editor", "viewer", "eligibleTarget", "ineligibleTarget"], token);
        var workersB = await WaitForGenuineProjectionsAsync(manifest, manifest.TenantB, ["crossTenantTarget"], token);
        manifest.State["projectionWorkerIds"] = JsonSerializer.SerializeToNode(workersA.Concat(workersB)
            .ToDictionary(x => x.Key, x => x.Value));
        manifest.State["defaultAssigneeMembershipId"] = manifest.Membership("editor").ToString("D");
        manifest.Save();

        foreach (var (organisation, label, logical, workers) in new[]
        {
            (manifest.TenantA, "A", "editor", workersA),
            (manifest.TenantB, "B", "crossTenantTarget", workersB)
        })
        {
            var ids = await AuthorizedAsync(manifest, organisation, "Clients.Create", async (database, ct) =>
            {
                var workerId = workers[logical];
                var worker = await database.SocialWorkers.SingleAsync(x => x.Id == workerId, ct);
                var beneficiary = SyntheticClient.Create(SyntheticClient.Niss(sequence: label == "A" ? 701 : 703));
                beneficiary.ReferenceNumber = manifest.Marker("beneficiary" + label);
                beneficiary.SocialWorkerName = "Synthetic worker";
                database.Add(beneficiary);
                worker.AddSocialCase(beneficiary, new DateTime(2026, 1, 1), manifest.Marker("case" + label));
                await database.SaveChangesAsync(ct);
                var assigned = await database.SocialCases.SingleAsync(x => x.ClientId == beneficiary.Id, ct);
                if (assigned.OrganisationId != organisation || assigned.SocialWorkerId != workerId)
                    throw new InvalidOperationException("Synthetic assignment ownership mismatch.");
                return (Beneficiary: beneficiary.Id, Case: assigned.Id);
            }, token);
            manifest.State["beneficiary" + label + "Id"] = ids.Beneficiary;
            manifest.State["case" + label + "Id"] = ids.Case;
            manifest.Save();
        }

        var unassigned = await AuthorizedAsync(manifest, manifest.TenantA, "Clients.Create", async (database, ct) =>
        {
            var beneficiary = SyntheticClient.Create(SyntheticClient.Niss(sequence: 705));
            beneficiary.ReferenceNumber = manifest.Marker("unassigned");
            database.Add(beneficiary);
            await database.SaveChangesAsync(ct);
            Assert.False(await database.SocialCases.AnyAsync(x => x.ClientId == beneficiary.Id, ct));
            return beneficiary.Id;
        }, token);
        manifest.State["unassignedId"] = unassigned;
        manifest.Save();
    }

    private static async Task ReadOwnedAsync(B2FixtureManifest manifest, CancellationToken token)
    {
        foreach (var (organisation, label, logical) in new[]
        { (manifest.TenantA, "A", "editor"), (manifest.TenantB, "B", "crossTenantTarget") })
        {
            await AuthorizedAsync(manifest, organisation, "Clients.ViewAll", async (database, ct) =>
            {
                var id = manifest.State["beneficiary" + label + "Id"]!.GetValue<int>();
                var caseId = manifest.State["case" + label + "Id"]!.GetValue<int>();
                var beneficiary = await database.Clients.SingleAsync(x => x.Id == id, ct);
                Assert.Equal(manifest.Marker("beneficiary" + label), beneficiary.ReferenceNumber);
                Assert.Equal(organisation, beneficiary.OrganisationId);
                var support = await database.SocialCases.Include(x => x.SocialWorker).SingleAsync(x => x.Id == caseId, ct);
                Assert.Equal(id, support.ClientId);
                Assert.Equal(manifest.Membership(logical), support.SocialWorker.OrganisationMembershipId);
                Assert.Equal(organisation, support.OrganisationId);
                Assert.False(support.SocialWorker.Softdelete);
                var other = manifest.State["beneficiary" + (label == "A" ? "B" : "A") + "Id"]!.GetValue<int>();
                Assert.False(await database.Clients.AnyAsync(x => x.Id == other, ct));
                return true;
            }, token);
        }
        await AuthorizedAsync(manifest, manifest.TenantA, "Clients.ViewAll", async (database, ct) =>
        {
            var id = manifest.State["unassignedId"]!.GetValue<int>();
            Assert.Equal(manifest.Marker("unassigned"), (await database.Clients.SingleAsync(x => x.Id == id, ct)).ReferenceNumber);
            Assert.False(await database.SocialCases.AnyAsync(x => x.ClientId == id, ct));
            return true;
        }, token);
    }

    private static async Task TeardownOwnedAsync(B2FixtureManifest manifest, CancellationToken token)
    {
        foreach (var (organisation, label) in new[] { (manifest.TenantA, "A"), (manifest.TenantB, "B") })
        {
            await AuthorizedAsync(manifest, organisation, "Clients.Delete", async (database, ct) =>
            {
                var owned = new[] { ("beneficiary" + label + "Id", "beneficiary" + label) }
                    .Concat(label == "A" ? new[] { ("unassignedId", "unassigned") } : Array.Empty<(string, string)>());
                foreach (var (key, marker) in owned)
                {
                    if (manifest.State[key] is null) continue;
                    var id = manifest.State[key]!.GetValue<int>();
                    var beneficiary = await database.Clients.SingleOrDefaultAsync(x => x.Id == id, ct);
                    if (beneficiary is null) continue;
                    if (beneficiary.ReferenceNumber != manifest.Marker(marker))
                        throw new InvalidOperationException("B2 beneficiary ownership mismatch.");
                    database.RemoveRange(await database.SocialCases.Where(x => x.ClientId == id).ToListAsync(ct));
                    database.Remove(beneficiary);
                }
                await database.SaveChangesAsync(ct);
                var members = label == "A" ? new[] { "editor", "viewer", "eligibleTarget", "ineligibleTarget" } : new[] { "crossTenantTarget" };
                foreach (var logical in members)
                {
                    var membership = manifest.Membership(logical);
                    var worker = await database.SocialWorkers.SingleOrDefaultAsync(x => x.OrganisationMembershipId == membership, ct);
                    if (worker is null) continue;
                    if (worker.UserName != manifest.Marker(logical)
                        || await database.SocialCases.AnyAsync(x => x.SocialWorkerId == worker.Id, ct))
                        throw new InvalidOperationException("B2 projection ownership or remaining assignment conflict.");
                    // Deleting a run-owned consumed projection is cleanup; initialization never writes SocialWorker state.
                    database.Remove(worker);
                }
                await database.SaveChangesAsync(ct);
                return true;
            }, token);
        }
        manifest.State["teardownComplete"] = true;
        manifest.Save();
    }
}

internal sealed class B2FixtureManifest
{
    internal JsonObject Document { get; }
    internal string FileName { get; }
    internal string Root { get; }
    internal string RunId => Document["runId"]!.GetValue<string>();
    internal JsonObject Fixtures => Document["fixtures"]!.AsObject();
    internal JsonObject State => Fixtures[Service]!.AsObject();
    internal string Service { get; }
    internal Guid TenantA => Guid.Parse(Fixtures["tenantA"]!.GetValue<string>());
    internal Guid TenantB => Guid.Parse(Fixtures["tenantB"]!.GetValue<string>());

    private B2FixtureManifest(JsonObject document, string fileName, string service)
    { Document = document; FileName = fileName; Service = service; Root = document["privateRoot"]!.GetValue<string>(); }

    internal static bool Selected(string operation, string? selected)
    {
        if (string.IsNullOrWhiteSpace(selected)) return false;
        if (selected is not ("VerifyCatalog" or "Initialize" or "Readback" or "Teardown"))
            throw new InvalidOperationException("Unadmitted B2 operation.");
        return StringComparer.Ordinal.Equals(operation, selected);
    }

    internal static void ValidateTarget(JsonObject document, string expectedSource)
    {
        if (document["schemaVersion"]?.GetValue<int>() != 1
            || document["sourceBaseline"]?.GetValue<string>() != "198a66015a5a90e2936f3caa263774d74011d1ec"
            || document["environment"]?.GetValue<string>() != "Development"
            || document["exclusiveTarget"]?.GetValue<bool>() != true
            || !System.Text.RegularExpressions.Regex.IsMatch(expectedSource, "^[0-9a-f]{40}$")
            || document["sourceCommit"]?.GetValue<string>() != expectedSource)
            throw new InvalidOperationException("B2 source or Development admission failed.");
        var postgres = document["postgres"]?.AsObject() ?? throw new InvalidOperationException("Missing B2 target.");
        if (postgres["host"]?.GetValue<string>() != "127.0.0.1" || postgres["port"]?.GetValue<int>() != 5432
            || postgres["authDatabase"]?.GetValue<string>() != "zekaAuthdb"
            || postgres["adminDatabase"]?.GetValue<string>() != "zekaAdminAreadb"
            || postgres["clientDatabase"]?.GetValue<string>() != "zekaClientdb"
            || !System.Text.RegularExpressions.Regex.IsMatch(postgres["containerId"]?.GetValue<string>() ?? "", "^[0-9a-f]{64}$"))
            throw new InvalidOperationException("B2 target mismatch.");
    }

    internal static B2FixtureManifest Load(string service)
    {
        foreach (var variable in new[] { "DOTNET_ENVIRONMENT", "ASPNETCORE_ENVIRONMENT" })
        {
            var environment = Environment.GetEnvironmentVariable(variable);
            if (environment is not null && environment != "Development")
                throw new InvalidOperationException("B2 helpers reject non-Development process environments.");
        }
        var path = Environment.GetEnvironmentVariable("ZEKA_B2_MANIFEST")
            ?? throw new InvalidOperationException("An explicit B2 manifest is required.");
        var fullManifest = Path.GetFullPath(path);
        var privateBase = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zeka", "B2"));
        if (!fullManifest.StartsWith(privateBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || Path.GetFileName(fullManifest) != "manifest.json" || !File.Exists(fullManifest)
            || new FileInfo(fullManifest).Length > 1_048_576)
            throw new InvalidOperationException("Unadmitted B2 manifest path.");
        for (var cursor = fullManifest; cursor is not null; cursor = Path.GetDirectoryName(cursor))
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("B2 manifest must not traverse reparse points.");
            if (StringComparer.OrdinalIgnoreCase.Equals(cursor, privateBase)) break;
        }
        var document = JsonNode.Parse(File.ReadAllText(fullManifest))?.AsObject()
            ?? throw new InvalidOperationException("Invalid B2 manifest.");
        ValidateTarget(document, Environment.GetEnvironmentVariable("ZEKA_B2_SOURCE_COMMIT") ?? "");
        var run = document["runId"]?.GetValue<string>() ?? "";
        if (!System.Text.RegularExpressions.Regex.IsMatch(run, "^[A-Za-z0-9][A-Za-z0-9_-]{7,95}$"))
            throw new InvalidOperationException("Invalid B2 run identity.");
        var root = Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Zeka", "B2", run));
        if (!StringComparer.OrdinalIgnoreCase.Equals(root, Path.GetFullPath(document["privateRoot"]!.GetValue<string>()))
            || !StringComparer.OrdinalIgnoreCase.Equals(Path.GetFullPath(path), Path.Combine(root, "manifest.json")))
            throw new InvalidOperationException("B2 private-root mismatch.");
        var result = new B2FixtureManifest(document, Path.GetFullPath(path), service);
        result.ExistingPrivatePath(path);
        if (result.TenantA == Guid.Empty || result.TenantB == Guid.Empty || result.TenantA == result.TenantB)
            throw new InvalidOperationException("Invalid synthetic tenant identities.");
        if (result.Fixtures[service] is null) result.Fixtures[service] = new JsonObject();
        return result;
    }

    internal string ExistingPrivatePath(string path)
    {
        var full = Path.GetFullPath(path);
        if (!full.StartsWith(Path.GetFullPath(Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(full) || new FileInfo(full).Length > 1_048_576)
            throw new InvalidOperationException("B2 private file admission failed.");
        for (var cursor = full; cursor is not null; cursor = Path.GetDirectoryName(cursor))
        {
            if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("B2 private files must not traverse reparse points.");
            if (StringComparer.OrdinalIgnoreCase.Equals(cursor, Path.GetFullPath(Root))) break;
        }
        return full;
    }

    internal string PathFor(string key) => ExistingPrivatePath(Document["paths"]![key]!.GetValue<string>());
    internal Guid Membership(string logical) => Guid.Parse(Fixtures[logical]!["membershipId"]!.GetValue<string>());
    internal string Marker(string logical) => "b2-" + RunId + "-" + logical;

    internal B2OperatorSession Session(Guid organisation)
    {
        var session = JsonNode.Parse(File.ReadAllText(PathFor("operatorSession")))!.AsObject();
        var subject = session["subjectId"]!.GetValue<string>();
        var expected = Fixtures["operator"]!.AsObject();
        var expectedMembership = expected[organisation == TenantA ? "membershipAId" : "membershipBId"]!.GetValue<string>();
        if (subject != expected["userId"]!.GetValue<string>()
            || !Guid.TryParse(subject, out var subjectId) || subjectId == Guid.Empty
            || session["expiresAtUtc"]!.GetValue<DateTimeOffset>() <= DateTimeOffset.UtcNow.AddSeconds(30)
            || !session["organisationIds"]!.AsArray().Any(value => value!.GetValue<string>() == organisation.ToString("D"))
            || session["membershipIds"]![organisation.ToString("D")]!.GetValue<string>() != expectedMembership
            || string.IsNullOrWhiteSpace(session["accessToken"]!.GetValue<string>()))
            throw new InvalidOperationException("A live matching synthetic operator session is required.");
        return new(subject, organisation, session["accessToken"]!.GetValue<string>(), Guid.Parse(expectedMembership));
    }

    internal ConfigurationManager Configuration(string service, string runtimeEnvironment)
    {
        var configuration = new ConfigurationManager();
        configuration.AddJsonFile(PathFor(service + "Config"), optional: false, reloadOnChange: false);
        var connection = Environment.GetEnvironmentVariable(runtimeEnvironment)
            ?? throw new InvalidOperationException("Own runtime credential delivery is missing.");
        var parsed = new NpgsqlConnectionStringBuilder(connection);
        var database = service == "admin" ? "zekaAdminAreadb" : "zekaClientdb";
        var role = service == "admin" ? "zeka_adminarea_runtime" : "zeka_client_runtime";
        if (parsed.Host != "127.0.0.1" || parsed.Port != 5432 || parsed.Database != database || parsed.Username != role
            || !string.IsNullOrWhiteSpace(parsed.Options) || parsed.IncludeErrorDetail)
            throw new InvalidOperationException("Own runtime connection mismatch.");
        configuration["ConnectionStrings:ClientApiConnection"] = connection;
        if (Document["auth"]?["baseUrl"]?.GetValue<string>() != "https://localhost:19444")
            throw new InvalidOperationException("Exact loopback HTTPS Auth authority is required.");
        // The host helper uses the already approved loopback Auth endpoint, with identical TLS/authority checks.
        configuration["TenantAuthorization:AuthManagementUrl"] = "https://localhost:19444";
        return configuration;
    }

    internal HttpClientHandler TrustedHandler()
    {
        var certificate = X509Certificate2.CreateFromPemFile(PathFor("caPem"));
        return new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false,
            ServerCertificateCustomValidationCallback = (_, leaf, _, errors) =>
            {
                if (leaf is null || (errors & (SslPolicyErrors.RemoteCertificateNameMismatch
                    | SslPolicyErrors.RemoteCertificateNotAvailable)) != 0) return false;
                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.CustomTrustStore.Add(certificate);
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.ApplicationPolicy.Add(new System.Security.Cryptography.Oid("1.3.6.1.5.5.7.3.1"));
                return chain.Build(leaf);
            }
        };
    }

    internal async Task VerifyRuntimeAsync(string connection, string role, string database, CancellationToken token)
    {
        await new Zeka.PersistenceSecurity.RuntimeDatabaseIdentityValidator(connection, role).StartAsync(token);
        await using var db = new NpgsqlConnection(connection);
        await db.OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            select current_user = @role and current_database() = @database
              and not r.rolsuper and not r.rolbypassrls and not r.rolcreatedb and not r.rolcreaterole
              and not exists(select 1 from pg_catalog.pg_auth_members m where m.member = r.oid)
            from pg_catalog.pg_roles r where r.rolname = current_user
            """, db) { CommandTimeout = 10 };
        command.Parameters.AddWithValue("role", role);
        command.Parameters.AddWithValue("database", database);
        if (await command.ExecuteScalarAsync(token) is not true)
            throw new InvalidOperationException("Restricted B2 runtime role admission failed.");
    }

    internal void Save()
    {
        // Only the helper's own non-secret state is changed; no session/config/token is serialized.
        var lockPath = Path.Combine(Root, "private", "manifest.lock");
        var lockParent = Path.GetDirectoryName(lockPath)!;
        if (!Directory.Exists(lockParent)
            || (File.GetAttributes(lockParent) & FileAttributes.ReparsePoint) != 0
            || File.Exists(lockPath) && (File.GetAttributes(lockPath) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("B2 manifest lock custody is invalid.");
        using var held = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        ExistingPrivatePath(FileName);
        var latest = JsonNode.Parse(File.ReadAllText(FileName))!.AsObject();
        ValidateTarget(latest, Document["sourceCommit"]!.GetValue<string>());
        if (latest["runId"]!.GetValue<string>() != RunId) throw new InvalidOperationException("B2 manifest changed.");
        latest["fixtures"]![Service] = State.DeepClone();
        var scratch = FileName + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(scratch, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream))
                writer.Write(latest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            // Same-volume atomic replacement keeps the H1 read-only producer from observing partial JSON.
            File.Move(scratch, FileName, overwrite: true);
        }
        finally
        {
            if (File.Exists(scratch)) File.Delete(scratch);
        }
    }
}

internal sealed record B2OperatorSession(string SubjectId, Guid SelectedOrganisationId, string BearerToken,
    Guid ExpectedMembershipId) : IOperationIdentity, ITenantAccessCredential
{
    public override string ToString() => "B2OperatorSession[private]";
}
