using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AuthManager.Application.Authorization;
using AuthManager.Application.Lifecycle;
using AuthManager.Core.Enums;
using AuthManager.Core.Lifecycle;
using AuthManager.Core.Organisations;
using AuthManager.Infrastructure.Identity.Models;
using AuthManager.Infrastructure.Lifecycle;
using AuthManager.Infrastructure.Persistence;
using AuthManager.Infrastructure.Persistence.Lifecycle;
using AuthManager.Infrastructure.Persistence.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Zeka.Lifecycle.Contracts;

namespace Infrastructure.IntegrationTests;

[Collection(PostgreSqlCollection.CollectionName)]
[Trait("Issue", "46")]
[Trait("Evidence", "LIFE-01")]
public sealed class LifecycleExportCoordinatorEvidenceTests(PostgreSqlFixture fixture)
{
    private const string MigratorPassword = "synthetic-life01-migrator";
    private const string RuntimePassword = "synthetic-life01-runtime";
    private static readonly Guid OrganisationId = Guid.Parse("46010000-0000-0000-0000-000000000001");
    private static readonly Guid RegistryRevision = Guid.Parse("46010000-0000-0000-0000-000000000002");
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Provider_real_restart_safe_export_reconciles_fences_fragments_package_and_release()
    {
        var environment = await CreateEnvironmentAsync();
        var registry = Registry();
        await using (var activation = new AuthDbContext(Options(environment.MigratorConnection)))
        {
            await activation.Database.OpenConnectionAsync();
            await activation.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            Assert.True(await new ReviewedLifecycleRegistryActivation(activation)
                .ActivateAsync(registry, 0, default));
        }

        var admission = await new LifecycleAdmission(new CurrentAccess(),
            new LifecycleAdmissionStore(Options(environment.RuntimeConnection), new ManualTimeProvider(Now)))
            .AdmitAsync(environment.OwnerUserId, OrganisationId, LifecycleOperationFamily.Export, Guid.NewGuid());
        var operation = Assert.IsType<LifecycleOperation>(admission.Operation);
        Assert.NotNull(operation.ExportInventoryJson);
        Assert.Equal(64, operation.ExportInventoryHash!.Length);
        Assert.Equal(21, JsonDocument.Parse(operation.ExportInventoryJson!).RootElement.GetArrayLength());
        var artifactStore = new InMemoryExportArtifactStore();
        var packageSink = new FailAfterFirstStorePackageSink();
        var clock = new ManualTimeProvider(Now.AddMinutes(1));
        LifecycleExportStore Store(IReviewedExportCategoryInventory? inventory = null) => new(Options(environment.RuntimeConnection),
            new DeterministicExportPackageAssembler(), artifactStore, packageSink,
            inventory ?? new ReviewedExportCategoryInventoryV1(), clock);

        var coordinator = new LifecycleExportCoordinator(Store());
        Assert.Null(await RuntimeUpdate(environment.RuntimeConnection, environment.OwnerMembershipId));
        Assert.Equal(ExportProgressStatus.Progressed,
            (await coordinator.BeginAsync(operation.Id, OrganisationId)).Status);
        coordinator = new LifecycleExportCoordinator(Store(new InventoryMustNotBeConsultedAfterAdmission()));
        Assert.Equal([operation.Id], await Store().RecoverableAsync(OrganisationId, clock.GetUtcNow(), default));
        var recovered = Assert.Single(await coordinator.RecoverAsync(OrganisationId, clock.GetUtcNow()));
        Assert.Equal(ExportProgressStatus.AwaitingParticipants, recovered.Status);

        var fenceRevision = 2L;
        var fenceTokens = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth-management"] = "auth-fence-1",
            ["admin-area"] = "admin-fence-1",
            ["client-management"] = "client-fence-1"
        };
        var participantClock = new ManualTimeProvider(Now.AddSeconds(30));
        var authParticipant = new AuthExportParticipant(Options(environment.RuntimeConnection), artifactStore,
            participantClock);
        OrganisationExportFenceEnteredV1? firstReceipt = null;
        var fenceReceipts = new List<OrganisationExportFenceEnteredV1>();
        foreach (var participant in fenceTokens.Keys.Order(StringComparer.Ordinal))
        {
            var header = Header(operation.Id, fenceRevision, participant);
            var enter = new EnterOrganisationExportFenceV1(header);
            OrganisationExportFenceEnteredV1 receipt;
            if (participant == AuthExportInventoryV1.ParticipantId)
            {
                var concurrent = await Task.WhenAll(
                    authParticipant.EnterAsync(enter),
                    new AuthExportParticipant(Options(environment.RuntimeConnection), artifactStore, participantClock)
                        .EnterAsync(enter));
                receipt = concurrent[0];
                Assert.Equal(receipt.Header.MessageId, concurrent[1].Header.MessageId);
                Assert.Equal(receipt.ReceiptHash, concurrent[1].ReceiptHash);
                fenceTokens[participant] = receipt.FenceToken;
            }
            else receipt = new OrganisationExportFenceEnteredV1(
                header, fenceTokens[participant], 1, Now.AddSeconds(30));
            fenceReceipts.Add(receipt);
            firstReceipt ??= receipt;
            var result = await coordinator.ReceiveAsync(receipt);
            Assert.Contains(result.Status,
                [ExportProgressStatus.AwaitingParticipants, ExportProgressStatus.Progressed]);
        }
        Assert.Equal(ExportProgressStatus.Replay,
            (await coordinator.ReceiveAsync(firstReceipt!)).Status);

        // A new store/coordinator instance proves that no process-local state is required after fencing.
        coordinator = new LifecycleExportCoordinator(Store(new InventoryMustNotBeConsultedAfterAdmission()));
        var blocked = await RuntimeUpdate(environment.RuntimeConnection, environment.OwnerMembershipId);
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState, blocked);

        var requirements = fenceTokens.Keys.Order(StringComparer.Ordinal)
            .Select(x => new ExportFenceParticipantRequirementV1(x, 1));
        var fenceEvidence = new CompleteExportFenceEvidenceV1(RegistryRevision,
            operation.InventoryHash, requirements, fenceReceipts.Select(x => new ExportFenceReceiptV1(x)));
        var authStage = new StageOrganisationExportV1(
            Header(operation.Id, 2, "auth-management"), clock.GetUtcNow(), fenceEvidence);
        var authFragment = await authParticipant.StageAsync(authStage);
        var replayedAuthFragment = await new AuthExportParticipant(Options(environment.RuntimeConnection),
            artifactStore, participantClock).StageAsync(authStage);
        Assert.Equal(authFragment.FragmentHash, replayedAuthFragment.FragmentHash);
        Assert.Equal(JsonSerializer.Serialize(authFragment), JsonSerializer.Serialize(replayedAuthFragment));
        var acceptedAuth = await coordinator.ReceiveAsync(authFragment);
        Assert.True(acceptedAuth.Status == ExportProgressStatus.AwaitingParticipants,
            $"Auth fragment status={acceptedAuth.Status}; headerRevision={authFragment.Header.OperationRevision}; " +
            $"snapshot={authFragment.SnapshotAt:O}; state={await RuntimeScalar(environment.RuntimeConnection, "SELECT \"State\"::text || '|' || \"Revision\"::text || '|' || \"SnapshotAt\"::text FROM public.\"OrganisationLifecycleOperations\"")}; " +
            $"fence={await RuntimeScalar(environment.RuntimeConnection, "SELECT \"FenceToken\" FROM public.\"LifecycleExportFenceReceipts\" WHERE \"ParticipantId\"='auth-management'")}");
        var incompleteClient = new OrganisationExportFragmentReadyV1(
            Header(operation.Id, 2, "client-management"), clock.GetUtcNow(),
            fenceTokens["client-management"], [await Empty("client-management", "beneficiaries", artifactStore)]);
        Assert.Equal(ExportProgressStatus.Conflict,
            (await coordinator.ReceiveAsync(incompleteClient)).Status);

        var fragments = new[]
        {
            await Fragment(operation.Id, "admin-area", fenceTokens["admin-area"],
                ["partner-contacts", "partner-document-metadata", "partner-emails", "partners", "staff-members", "teams"],
                artifactStore),
            await Fragment(operation.Id, "admin-area-documents", fenceTokens["admin-area"],
                ["partner-document-artifacts"], artifactStore),
            await ClientFragment(operation.Id, fenceTokens["client-management"], artifactStore)
        };
        await coordinator.ReceiveAsync(fragments[0]);
        await coordinator.ReceiveAsync(fragments[1]);
        await Assert.ThrowsAsync<IOException>(() => coordinator.ReceiveAsync(fragments[2]));
        coordinator = new LifecycleExportCoordinator(Store(new InventoryMustNotBeConsultedAfterAdmission()));
        var packageRecovery = Assert.Single(await coordinator.RecoverAsync(OrganisationId, clock.GetUtcNow()));
        Assert.Equal(ExportProgressStatus.Progressed, packageRecovery.Status);
        Assert.Equal(2, packageSink.Attempts);
        Assert.Single(packageSink.ObservedHashes.Distinct(StringComparer.Ordinal));
        Assert.True(packageSink.ObservedContents[0].AsSpan().SequenceEqual(packageSink.ObservedContents[1]));

        var persistedHash = Assert.IsType<string>(await RuntimeScalar(environment.RuntimeConnection,
            "SELECT \"PackageSha256\" FROM public.\"LifecycleExportPackages\""));
        var bytes = packageSink.Read(operation.Id).ToArray();
        Assert.Equal(persistedHash, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
        using (var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read))
        {
            var manifest = archive.GetEntry("manifest.json");
            Assert.NotNull(manifest);
            using var document = await JsonDocument.ParseAsync(manifest!.Open());
            Assert.Equal(21, document.RootElement.GetProperty("categories").GetArrayLength());
            Assert.Equal(RegistryRevision, document.RootElement.GetProperty("registryRevision").GetGuid());
            Assert.Equal(operation.ExportInventoryHash,
                document.RootElement.GetProperty("exportInventoryHash").GetString());
            Assert.Equal(environment.OwnerUserId,
                document.RootElement.GetProperty("requestedBy").GetGuid());
            Assert.Equal("completed", document.RootElement.GetProperty("completionStatus").GetString());
            Assert.All(document.RootElement.GetProperty("categories").EnumerateArray(),
                category => Assert.Equal(64, category.GetProperty("fragmentHash").GetString()!.Length));
            Assert.Equal(4, document.RootElement.GetProperty("categories").EnumerateArray()
                .Select(category => category.GetProperty("fragmentHash").GetString())
                .Distinct(StringComparer.Ordinal).Count());
        }
        Assert.Equal(4L, await RuntimeScalar(environment.RuntimeConnection,
            "SELECT count(DISTINCT \"FragmentHash\") FROM public.\"LifecycleExportFragments\""));

        var unknownRelease = new OrganisationExportFenceReleasedV1(
            new LifecycleMessageHeaderV1(operation.Id, OrganisationId, 2, "unknown-participant", 1,
                Guid.NewGuid(), operation.Id, operation.Id), "unknown-fence", Now.AddMinutes(2));
        Assert.Equal(ExportProgressStatus.Conflict,
            (await coordinator.ReceiveAsync(unknownRelease)).Status);
        var crossOrganisationRelease = new OrganisationExportFenceReleasedV1(
            new LifecycleMessageHeaderV1(operation.Id, Guid.NewGuid(), 2, "auth-management", 1,
                Guid.NewGuid(), operation.Id, operation.Id), fenceTokens["auth-management"], Now.AddMinutes(2));
        Assert.Equal(ExportProgressStatus.Rejected,
            (await coordinator.ReceiveAsync(crossOrganisationRelease)).Status);

        foreach (var participant in fenceTokens.Keys.Order(StringComparer.Ordinal))
        {
            var releaseCommand = new ReleaseOrganisationExportFenceV1(
                Header(operation.Id, 2, participant), fenceTokens[participant]);
            OrganisationExportFenceReleasedV1 release;
            if (participant == AuthExportInventoryV1.ParticipantId)
            {
                var concurrent = await Task.WhenAll(
                    authParticipant.ReleaseAsync(releaseCommand),
                    new AuthExportParticipant(Options(environment.RuntimeConnection), artifactStore, participantClock)
                        .ReleaseAsync(releaseCommand));
                release = concurrent[0];
                Assert.Equal(release.Header.MessageId, concurrent[1].Header.MessageId);
                Assert.Equal(release.ReleasedAt, concurrent[1].ReleasedAt);
            }
            else release = new OrganisationExportFenceReleasedV1(
                Header(operation.Id, 2, participant), fenceTokens[participant], Now.AddMinutes(2));
            await coordinator.ReceiveAsync(release);
        }
        Assert.Equal($"{(int)LifecycleOperationState.Completed}|false", await RuntimeScalar(
            environment.RuntimeConnection,
            "SELECT \"State\"::text || '|' || \"IsActive\"::text FROM public.\"OrganisationLifecycleOperations\""));
        Assert.Equal(21L, await RuntimeScalar(environment.RuntimeConnection,
            "SELECT sum(pg_catalog.jsonb_array_length(\"CategoriesJson\")) FROM public.\"LifecycleExportFragments\""));
        Assert.Equal(3L, await RuntimeScalar(environment.RuntimeConnection,
            "SELECT count(*) FROM public.\"AuthExportParticipantInbox\""));
        Assert.Equal(3L, await RuntimeScalar(environment.RuntimeConnection,
            "SELECT count(*) FROM public.\"AuthExportParticipantOutbox\""));
        Assert.Equal($"{(int)AuthExportParticipantState.Released}|true", await RuntimeScalar(
            environment.RuntimeConnection,
            "SELECT \"State\"::text || '|' || (\"FragmentHash\" IS NOT NULL)::text FROM public.\"AuthExportParticipantExecutions\""));
        Assert.Equal(10L, await RuntimeScalar(environment.RuntimeConnection,
            $"SELECT count(*) FROM public.\"OutboxMessages\" WHERE \"CorrelationId\" = '{operation.Id:D}'"));
        Assert.Equal(0L, await RuntimeScalar(environment.RuntimeConnection,
            "SELECT count(*) FROM public.\"LifecycleExportPackages\"",
            Guid.Parse("46010000-0000-0000-0000-000000000099")));
        Assert.Null(await RuntimeUpdate(environment.RuntimeConnection, environment.OwnerMembershipId));
    }

    [Fact]
    public void Package_assembly_is_byte_deterministic_and_rejects_unreferenced_bytes()
    {
        var bytes = Encoding.UTF8.GetBytes("id,status\n");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var category = new ExportPackageCategory("auth-management", "memberships", "empty", 0,
            "auth-memberships-v1", hash, "fragments/auth-management/memberships.csv", null,
            new string('c', 64));
        var input = new ExportPackageInput(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new string('a', 64),
            new string('d', 64), Now, new string('b', 64), Guid.NewGuid(), Now.AddMinutes(-1), Now,
            "completed", [category],
            new Dictionary<string, byte[]> { [category.ArtifactReference!] = bytes });
        var assembler = new DeterministicExportPackageAssembler();
        var first = assembler.Assemble(input);
        var second = assembler.Assemble(input);
        Assert.Equal(first.PackageSha256, second.PackageSha256);
        Assert.Equal(first.Content, second.Content);
        var invalid = input with { Artifacts = new Dictionary<string, byte[]>(input.Artifacts)
            { ["fragments/unreferenced.csv"] = [] } };
        Assert.Throws<InvalidOperationException>(() => assembler.Assemble(invalid));
        Assert.Throws<InvalidOperationException>(() => assembler.Assemble(input with
        {
            Categories = [category with { ArtifactReference = "manifest.json" }],
            Artifacts = new Dictionary<string, byte[]> { ["manifest.json"] = bytes }
        }));
        Assert.Throws<InvalidOperationException>(() => assembler.Assemble(input with
        {
            Categories = [category, category with { Category = "second", ArtifactReference = "FRAGMENTS/auth-management/memberships.csv" }],
            Artifacts = new Dictionary<string, byte[]>
            {
                [category.ArtifactReference!] = bytes,
                ["FRAGMENTS/auth-management/memberships.csv"] = bytes
            }
        }));
    }

    [Fact]
    public async Task Future_skewed_fence_receipt_cannot_establish_snapshot_before_complete_fence()
    {
        var environment = await CreateEnvironmentAsync();
        var registry = Registry();
        await using (var activation = new AuthDbContext(Options(environment.MigratorConnection)))
        {
            await activation.Database.OpenConnectionAsync();
            await activation.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            Assert.True(await new ReviewedLifecycleRegistryActivation(activation).ActivateAsync(registry, 0, default));
        }
        var admission = await new LifecycleAdmission(new CurrentAccess(),
            new LifecycleAdmissionStore(Options(environment.RuntimeConnection), new ManualTimeProvider(Now)))
            .AdmitAsync(environment.OwnerUserId, OrganisationId, LifecycleOperationFamily.Export, Guid.NewGuid());
        var operation = Assert.IsType<LifecycleOperation>(admission.Operation);
        var store = new LifecycleExportStore(Options(environment.RuntimeConnection),
            new DeterministicExportPackageAssembler(), new InMemoryExportArtifactStore(),
            new InMemoryExportPackageSink(), new ReviewedExportCategoryInventoryV1(),
            new ManualTimeProvider(Now.AddMinutes(1)));
        var coordinator = new LifecycleExportCoordinator(store);
        await coordinator.BeginAsync(operation.Id, OrganisationId);
        foreach (var participant in new[] { "admin-area", "auth-management" })
            await coordinator.ReceiveAsync(new OrganisationExportFenceEnteredV1(
                Header(operation.Id, 2, participant), participant + "-fence", 1, Now.AddSeconds(30)));
        await Assert.ThrowsAsync<ArgumentException>(() => coordinator.ReceiveAsync(
            new OrganisationExportFenceEnteredV1(Header(operation.Id, 2, "client-management"),
                "client-fence", 1, Now.AddHours(1))));
        Assert.Equal($"{(int)LifecycleOperationState.EnteringFence}|2", await RuntimeScalar(
            environment.RuntimeConnection,
            "SELECT \"State\"::text || '|' || \"Revision\"::text FROM public.\"OrganisationLifecycleOperations\""));
        Assert.Equal(true, await RuntimeScalar(environment.RuntimeConnection,
            "SELECT \"SnapshotAt\" IS NULL FROM public.\"OrganisationLifecycleOperations\""));
    }

    [Fact]
    public async Task Membership_write_started_before_auth_fence_commits_before_snapshot_and_is_exported()
    {
        var environment = await CreateEnvironmentAsync();
        var grantClock = new ManualTimeProvider(Now.AddSeconds(5));
        var grants = new MembershipPermissionGrants(
            new MembershipPermissionGrantStore(Options(environment.RuntimeConnection), grantClock));
        var grantRequest = new MembershipPermissionGrantRequest(
            environment.OwnerUserId, environment.OwnerMembershipId, OrganisationId,
            environment.AdminMembershipId, TenantPermissions.OrganisationExport, "life01-export-grant");
        Assert.Equal(MembershipPermissionGrantStatus.Granted,
            (await grants.GrantAsync(grantRequest)).Status);
        grantClock.UtcNow = Now.AddSeconds(10);
        Assert.Equal(MembershipPermissionGrantStatus.Revoked,
            (await grants.RevokeAsync(grantRequest with { CorrelationId = "life01-export-revoke" })).Status);
        grantClock.UtcNow = Now.AddSeconds(15);
        Assert.Equal(MembershipPermissionGrantStatus.Granted,
            (await grants.GrantAsync(grantRequest with { CorrelationId = "life01-export-regrant" })).Status);
        var registry = Registry();
        await using (var activation = new AuthDbContext(Options(environment.MigratorConnection)))
        {
            await activation.Database.OpenConnectionAsync();
            await activation.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            Assert.True(await new ReviewedLifecycleRegistryActivation(activation).ActivateAsync(registry, 0, default));
        }
        var admission = await new LifecycleAdmission(new CurrentAccess(),
            new LifecycleAdmissionStore(Options(environment.RuntimeConnection), new ManualTimeProvider(Now)))
            .AdmitAsync(environment.OwnerUserId, OrganisationId, LifecycleOperationFamily.Export, Guid.NewGuid());
        var operation = Assert.IsType<LifecycleOperation>(admission.Operation);
        var artifactStore = new InMemoryExportArtifactStore();
        var coordinator = new LifecycleExportCoordinator(new LifecycleExportStore(
            Options(environment.RuntimeConnection), new DeterministicExportPackageAssembler(), artifactStore,
            new InMemoryExportPackageSink(), new ReviewedExportCategoryInventoryV1(),
            new ManualTimeProvider(Now.AddMinutes(1))));
        Assert.Equal(ExportProgressStatus.Progressed,
            (await coordinator.BeginAsync(operation.Id, OrganisationId)).Status);

        await using var writer = new NpgsqlConnection(environment.RuntimeConnection);
        await writer.OpenAsync();
        await using var writerTransaction = await writer.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
                         "SELECT pg_catalog.set_config('zeka.organisation_id', @organisation, true)",
                         writer, writerTransaction))
        {
            context.Parameters.AddWithValue("organisation", OrganisationId.ToString("D"));
            await context.ExecuteScalarAsync();
        }
        await using (var update = new NpgsqlCommand(
                         "UPDATE public.\"OrganisationMemberships\" SET \"PermissionSetId\"=@permission WHERE \"Id\"=@id",
                         writer, writerTransaction))
        {
            update.Parameters.AddWithValue("permission", PermissionSet.OrganisationAdministratorId);
            update.Parameters.AddWithValue("id", environment.OwnerMembershipId);
            Assert.Equal(1, await update.ExecuteNonQueryAsync());
        }

        var participantClock = new ManualTimeProvider(Now.AddSeconds(30));
        var authParticipant = new AuthExportParticipant(Options(environment.RuntimeConnection), artifactStore,
            participantClock);
        var authHeader = Header(operation.Id, 2, "auth-management");
        var enterTask = authParticipant.EnterAsync(new EnterOrganisationExportFenceV1(authHeader));
        await Task.Delay(TimeSpan.FromMilliseconds(200));
        Assert.False(enterTask.IsCompleted);
        await writerTransaction.CommitAsync();
        var authReceipt = await enterTask;

        var receipts = new[]
        {
            authReceipt,
            new OrganisationExportFenceEnteredV1(Header(operation.Id, 2, "admin-area"),
                "admin-fence", 1, Now.AddSeconds(30)),
            new OrganisationExportFenceEnteredV1(Header(operation.Id, 2, "client-management"),
                "client-fence", 1, Now.AddSeconds(30))
        };
        foreach (var receipt in receipts) await coordinator.ReceiveAsync(receipt);

        var snapshotValue = await RuntimeScalar(environment.RuntimeConnection,
            "SELECT \"SnapshotAt\" FROM public.\"OrganisationLifecycleOperations\"");
        var snapshotAt = snapshotValue switch
        {
            DateTimeOffset value => value,
            DateTime value => new DateTimeOffset(DateTime.SpecifyKind(value, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException("SnapshotAt was not persisted as a timestamp.")
        };
        Assert.True(authReceipt.EnteredAt <= snapshotAt);
        Assert.Equal(PostgresErrorCodes.ObjectNotInPrerequisiteState,
            await RuntimeGrant(environment.RuntimeConnection, environment.OwnerMembershipId,
                environment.OwnerUserId));
        var requirements = receipts.OrderBy(x => x.Header.ParticipantId, StringComparer.Ordinal)
            .Select(x => new ExportFenceParticipantRequirementV1(x.Header.ParticipantId, 1));
        var evidence = new CompleteExportFenceEvidenceV1(RegistryRevision, operation.InventoryHash,
            requirements, receipts.Select(x => new ExportFenceReceiptV1(x)));
        var fragment = await authParticipant.StageAsync(new StageOrganisationExportV1(
            Header(operation.Id, 2, "auth-management"), snapshotAt, evidence));
        var membership = fragment.Categories.Single(x => x.Category == AuthExportInventoryV1.Memberships);
        var csv = Encoding.UTF8.GetString((await artifactStore.ReadAsync(membership.ArtifactReference!, default)).Span);
        Assert.Contains(PermissionSet.OrganisationAdministratorId.ToString("D"), csv, StringComparison.Ordinal);
        var adminRow = Assert.Single(csv.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(line => line.StartsWith(environment.AdminMembershipId.ToString("D"), StringComparison.Ordinal)));
        Assert.EndsWith(",Organisations.Export", adminRow, StringComparison.Ordinal);
        Assert.Equal(1, adminRow.Split("Organisations.Export", StringSplitOptions.None).Length - 1);
    }

    private static async Task<OrganisationExportFragmentReadyV1> Fragment(Guid operationId,
        string participant, string fenceToken, string[] categories, InMemoryExportArtifactStore store)
    {
        var output = new List<ExportCategoryFragmentV1>();
        foreach (var category in categories)
            output.Add(await Empty(participant, category, store));
        return new(Header(operationId, 2, participant), Now.AddMinutes(1), fenceToken, output);
    }

    private static async Task<OrganisationExportFragmentReadyV1> ClientFragment(Guid operationId,
        string fenceToken, InMemoryExportArtifactStore store)
    {
        const string participant = "client-management";
        var materialized = new[] { "beneficiaries", "beneficiary-assignments", "case-histories",
            "professional-history", "school-history", "structured-assessments", "structured-reports" };
        var categories = new List<ExportCategoryFragmentV1>();
        foreach (var category in materialized) categories.Add(await Empty(participant, category, store));
        categories.Add(new("notes", ExportCategoryDispositionV1.Withheld, 0, null, null, null,
            "production-redaction-policy-unresolved"));
        foreach (var category in new[] { "generated-report-artifacts", "audit-events", "integration-events" })
            categories.Add(new(category, ExportCategoryDispositionV1.NotImplemented, 0, null, null, null,
                "category-not-implemented"));
        return new(Header(operationId, 2, participant), Now.AddMinutes(1), fenceToken, categories);
    }

    private static async Task<ExportCategoryFragmentV1> Empty(string participant, string category,
        InMemoryExportArtifactStore store)
    {
        var bytes = Encoding.UTF8.GetBytes("id\n");
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var reference = $"fragments/{participant}/{category}.csv";
        await store.StoreAsync(reference, hash, bytes, default);
        return new(category, ExportCategoryDispositionV1.Empty, 0, "fixture-v1", hash, reference, null);
    }

    private async Task<TestEnvironment> CreateEnvironmentAsync()
    {
        var administrator = await fixture.CreateDatabaseAsync();
        await Execute(administrator, Bootstrap());
        await Execute(administrator, $"ALTER ROLE zeka_auth_migrator PASSWORD '{MigratorPassword}'; ALTER ROLE zeka_auth_runtime PASSWORD '{RuntimePassword}';");
        var migrator = Connection(administrator, "zeka_auth_migrator", MigratorPassword);
        var runtime = Connection(administrator, "zeka_auth_runtime", RuntimePassword);
        await using (var database = new AuthDbContext(Options(migrator)))
        {
            await database.Database.OpenConnectionAsync();
            await database.Database.ExecuteSqlRawAsync("SET ROLE zeka_auth_owner");
            await database.GetService<IMigrator>().MigrateAsync();
        }
        await Execute(administrator, Bootstrap());

        var owner = User.Create("life01-owner@example.invalid", "life01-owner", "Synthetic", "Owner", Now);
        var admin = User.Create("life01-admin@example.invalid", "life01-admin", "Synthetic", "Admin", Now);
        owner.EmailConfirmed = true;
        admin.EmailConfirmed = true;
        var organisation = Organisation.Create(OrganisationId, "Synthetic LIFE-01", owner.Id, Now);
        Assert.True(organisation.Activate(Now));
        var membership = OrganisationMembership.CreateOwner(Guid.NewGuid(), OrganisationId, owner.Id, Now);
        var adminMembership = OrganisationMembership.Create(Guid.NewGuid(), OrganisationId, admin.Id,
            PermissionSet.OrganisationAdministratorId, Now);
        await using (var database = new AuthDbContext(Options(runtime)))
        {
            database.AddRange(owner, admin, organisation, membership, adminMembership);
            database.Entry(owner).Property(x => x.Status).CurrentValue = UserStatus.Active;
            database.Entry(admin).Property(x => x.Status).CurrentValue = UserStatus.Active;
            await database.SaveChangesAsync();
        }
        return new(migrator, runtime, owner.Id, membership.Id, adminMembership.Id);
    }

    private static LifecycleRegistry Registry()
    {
        var bindings = new[]
        {
            Binding("auth-management", OrganisationExportCapabilityV1.Fence, "auth-management"),
            Binding("admin-area", OrganisationExportCapabilityV1.Fence, "admin-area"),
            Binding("client-management", OrganisationExportCapabilityV1.Fence, "client-management"),
            Binding("auth-management", "auth-management.export-fragment", "auth-management"),
            Binding("admin-area", "admin-area.export-fragment", "admin-area"),
            Binding("admin-area-documents", "admin-area-documents.export-fragment", "admin-area"),
            Binding("client-management", "client-management.export-fragment", "client-management")
        };
        var validation = LifecycleRegistry.Validate(RegistryRevision, "Issue46-LIFE-01-v1",
            bindings.Select(x => x.Capability), bindings);
        Assert.Null(validation.Error);
        return Assert.IsType<LifecycleRegistry>(validation.Registry);
    }

    private static LifecycleBinding Binding(string participant, string key, string scope) =>
        new(new LifecycleCapability(LifecycleOperationFamily.Export, key, scope), participant, 1, true);

    private static LifecycleMessageHeaderV1 Header(Guid operationId, long revision, string participant) =>
        new(operationId, OrganisationId, revision, participant, 1, Guid.NewGuid(), operationId, operationId);

    private static async Task<string?> RuntimeUpdate(string connectionString, Guid membershipId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            "SELECT pg_catalog.set_config('zeka.organisation_id', @organisation, true)", connection, transaction))
        {
            context.Parameters.AddWithValue("organisation", OrganisationId.ToString("D"));
            await context.ExecuteScalarAsync();
        }
        await using var command = new NpgsqlCommand("UPDATE public.\"OrganisationMemberships\" SET \"ConcurrencyVersion\"=\"ConcurrencyVersion\"+1 WHERE \"Id\"=@id", connection, transaction);
        command.Parameters.AddWithValue("id", membershipId);
        try { await command.ExecuteNonQueryAsync(); await transaction.CommitAsync(); return null; }
        catch (PostgresException exception) { await transaction.RollbackAsync(); return exception.SqlState; }
    }

    private static async Task<string?> RuntimeGrant(string connectionString, Guid membershipId, Guid subjectId)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
                         "SELECT pg_catalog.set_config('zeka.organisation_id', @organisation, true)",
                         connection, transaction))
        {
            context.Parameters.AddWithValue("organisation", OrganisationId.ToString("D"));
            await context.ExecuteScalarAsync();
        }
        await using var command = new NpgsqlCommand("""
            INSERT INTO public."MembershipPermissionGrants"
              ("Id", "OrganisationId", "OrganisationMembershipId", "PermissionKey",
               "GrantedByMembershipId", "GrantedBySubjectId", "GrantedAtUtc", "ConcurrencyVersion")
            VALUES (@id, @organisation, @membership, 'Organisations.Export',
                    @membership, @subject, @at, 1)
            """, connection, transaction);
        command.Parameters.AddWithValue("id", Guid.NewGuid());
        command.Parameters.AddWithValue("organisation", OrganisationId);
        command.Parameters.AddWithValue("membership", membershipId);
        command.Parameters.AddWithValue("subject", subjectId);
        command.Parameters.AddWithValue("at", Now.AddMinutes(1));
        try { await command.ExecuteNonQueryAsync(); await transaction.CommitAsync(); return null; }
        catch (PostgresException exception) { await transaction.RollbackAsync(); return exception.SqlState; }
    }

    private static async Task<object?> RuntimeScalar(string connectionString, string sql, Guid? organisation = null)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using (var context = new NpgsqlCommand(
            "SELECT pg_catalog.set_config('zeka.organisation_id', @organisation, true)", connection, transaction))
        {
            context.Parameters.AddWithValue("organisation", (organisation ?? OrganisationId).ToString("D"));
            await context.ExecuteScalarAsync();
        }
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        var result = await command.ExecuteScalarAsync();
        await transaction.CommitAsync();
        return result;
    }

    private static DbContextOptions<AuthDbContext> Options(string connection) =>
        new DbContextOptionsBuilder<AuthDbContext>().UseNpgsql(connection).Options;
    private static string Connection(string administrator, string username, string password) =>
        new NpgsqlConnectionStringBuilder(administrator) { Username = username, Password = password, Pooling = false }.ConnectionString;
    private static async Task Execute(string connection, string sql)
    {
        await using var database = new NpgsqlConnection(connection);
        await database.OpenAsync();
        await using var command = new NpgsqlCommand(sql, database);
        await command.ExecuteNonQueryAsync();
    }
    private static string Bootstrap()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Deployments", "database", "bootstrap-auth-roles.sql");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            directory = directory.Parent;
        }
        throw new FileNotFoundException("Auth bootstrap not found.");
    }

    private sealed class CurrentAccess : ICurrentTenantAccess
    {
        public Task<CurrentTenantAccess> ResolveAsync(Guid subject, Guid organisation,
            CancellationToken cancellationToken) => Task.FromResult(new CurrentTenantAccess(
            TenantAccessOutcome.Authorized, organisation, Guid.NewGuid(), [LifecyclePermissions.Export],
            "synthetic-owner", Now));
    }

    private sealed class InventoryMustNotBeConsultedAfterAdmission : IReviewedExportCategoryInventory
    {
        public IReadOnlyList<ReviewedExportCategoryRequirement> RequirementsFor(string participantId) =>
            throw new InvalidOperationException("Current code-local category inventory must not be consulted.");
        public LifecycleExportInventory Freeze(LifecycleRegistry registry) =>
            throw new InvalidOperationException("Admission already froze the inventory.");
    }

    private sealed class FailAfterFirstStorePackageSink : IExportPackageSink
    {
        private readonly InMemoryExportPackageSink inner = new();
        public int Attempts { get; private set; }
        public List<string> ObservedHashes { get; } = [];
        public List<byte[]> ObservedContents { get; } = [];

        public async Task<string> StoreAsync(Guid operationId, string packageSha256,
            ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
        {
            Attempts++;
            ObservedHashes.Add(packageSha256);
            ObservedContents.Add(content.ToArray());
            var reference = await inner.StoreAsync(operationId, packageSha256, content, cancellationToken);
            if (Attempts == 1) throw new IOException("synthetic-crash-after-package-store");
            return reference;
        }

        public ReadOnlyMemory<byte> Read(Guid operationId) => inner.Read(operationId);
    }

    private sealed record TestEnvironment(string MigratorConnection, string RuntimeConnection,
        Guid OwnerUserId, Guid OwnerMembershipId, Guid AdminMembershipId);
}
