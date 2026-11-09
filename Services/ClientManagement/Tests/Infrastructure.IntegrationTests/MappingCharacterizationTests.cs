using System.Data.Common;
using System.Reflection;
using System.Text;
using ClientManagement.API.Services;
using ClientManagement.Application.Clients.Commands.UpSertIbisNumber;
using ClientManagement.Application.Clients.Model;
using ClientManagement.Application.Assessments.Queries.GetBilansList;
using ClientManagement.Application.Assessments.Queries.GetCurrentBilan;
using ClientManagement.Application.Assessments.Queries.GetLastBilan;
using ClientManagement.Application.Clients.Queries.GetClientDetail;
using ClientManagement.Application.Clients.Queries.GetClients;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Application.Common.Helpers;
using ClientManagement.Application.MonitoringActions.Queries.GetAll;
using ClientManagement.Application.MonitoringActions.Queries.GetById;
using ClientManagement.Application.QuarterlyMonitorings.Queries.GetById;
using ClientManagement.Application.QuarterlyMonitorings.Queries.GetList;
using ClientManagement.Application.QuarterlyMonitorings.Queries.GetListByBeneficiary;
using ClientManagement.Application.QuarterlyMonitorings.Queries.GetListByReferent;
using ClientManagement.Application.SchoolRegistations.Common;
using ClientManagement.Application.SchoolRegistations.Queries.GetSchoolList;
using ClientManagement.Application.Supports.Queries;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Enums;
using ClientManagement.Infrastructure.Persistence;
using ClientManagement.Tests.Common;
using Microsoft.AspNetCore.JsonPatch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Newtonsoft.Json;
using Npgsql;
using Xunit;
using Xunit.Abstractions;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace Infrastructure.IntegrationTests;

[Trait("Issue", "ZEK-39")]
public sealed class MappingCharacterizationTests(PostgreSqlClientRuntimeDatabase fixture, ITestOutputHelper output)
    : IClassFixture<PostgreSqlClientRuntimeDatabase>
{
    private static readonly JsonSerializerSettings Json = new MvcNewtonsoftJsonOptions().SerializerSettings;
    private readonly ReadCommands reads = new();
    private readonly EntityMaterializations materializations = new();

    [Fact]
    public async Task Read_projections_preserve_results_and_tenant_visibility()
    {
        var own = await SeedAsync("own", 680);
        var other = await SeedAsync("other", 690);
        var failures = new List<string>();

        var listActions = new GetMonitoringActionsQuery();
        await Observe("actions-list", own, listActions, "ReferenceData.View",
            r => new GetMonitoringActionsQueryHandler(r.MonitoringAction).Handle(listActions, default),
            x => Assert.Contains(x, a => a.ActionId == own.Action && a.ActionLabel == "Synthetic own action"));
        var byAction = new GetMonitoringActionById(own.Action);
        await Observe("action-id-global", other, byAction, "ReferenceData.View",
            r => new GetMonitoringActionByIdHandler(r.MonitoringAction).Handle(byAction, default),
            x => Assert.Equal("Synthetic own action", x.ActionLabel));

        var all = new GetQuarterlyMonitoringsQuery { PageNumber = 2, PageSize = 1 };
        await Observe("quarterly-page-2", own, all, "Clients.ViewAll",
            r => new GetQuarterlyMonitoringsQueryHandler(r).Handle(all, default),
            x => { Assert.Equal(2, x.TotalCount); Assert.Equal(own.OlderReport, Assert.Single(x.Items).QMonitoringId); });
        var assigned = new GetQuarterlyMonitoringsQuery();
        await Observe("quarterly-assigned", own, assigned, "Clients.ViewAssigned",
            r => new GetQuarterlyMonitoringsQueryHandler(r).Handle(assigned, default),
            x => Assert.All(x.Items, row => Assert.Equal(own.Client, row.ClientId)));

        var byClient = new GetQuarterlyMonitoringsByClientQuery
            { ClientId = own.Client, Filter = "Synthetic", WithDeleted = true };
        await Observe("quarterly-client-deleted", own, byClient, "Clients.ViewAll",
            r => new GetQuarterlyMonitoringsByClientQueryHandler(r).Handle(byClient, default),
            x =>
            {
                Assert.Equal(new[] { own.DeletedReport, own.NewerReport, own.OlderReport }, x.Items.Select(y => y.QMonitoringId));
                Assert.True(x.Items[0].Softdelete);
                Assert.Equal("2026Q2", x.Items[1].Quarter);
            });

        var byStaff = new GetQuarterlyMonitoringsByStaffMemberQuery
            { StaffMemberId = own.Worker, Filter = "Synthetic", PageSize = 1 };
        await Observe("quarterly-staff", own, byStaff, "Clients.ViewAll",
            r => new GetQuarterlyMonitoringsByStaffMemberQueryHandler(r).Handle(byStaff, default),
            x => { Assert.Equal(2, x.TotalCount); Assert.Equal(own.NewerReport, Assert.Single(x.Items).QMonitoringId); });

        var byReport = new GetQuarterlyMonitoringsByIdQuery(own.DeletedReport);
        await Observe("quarterly-id-deleted", own, byReport, "Clients.ViewAll",
            r => new GetQuarterlyMonitoringsByIdQueryHandler(r).Handle(byReport, default),
            x => { Assert.Equal(own.DeletedReport, x.QMonitoringId); Assert.True(x.Softdelete); });
        var foreignReport = new GetQuarterlyMonitoringsByIdQuery(other.OlderReport);
        await Observe("quarterly-id-foreign", own, foreignReport, "Clients.ViewAll",
            r => new GetQuarterlyMonitoringsByIdQueryHandler(r).Handle(foreignReport, default), x => Assert.Null(x));

        var school = new GetSchoolRegistrationsListQuery
            { ClientId = own.Client, Filter = "Synthetic", Orderby = "StartDate desc", PageNumber = 1, PageSize = 1 };
        await Observe("school-filter-order-page", own, school, "Clients.ViewAssigned",
            r => new GetSchoolRegistrationsListQuery.GetSchoolRegistrationsListQueryHandler(
                r, new SortHelper<SchoolRegistrationDto>()).Handle(school, default),
            x =>
            {
                Assert.Equal(2, x.TotalCount);
                var row = Assert.Single(x.Items);
                Assert.Equal(own.NewerSchool, row.SchoolRegistrationId);
                Assert.Null(row.Note);
                Assert.Equal("Synthetic own training", row.TrainingName);
            });

        var support = new GetSupportsListByClientQuery
            { ClientId = own.Client, Filter = "intentionally ignored", SortOrder = "desc", PageNumber = 1, PageSize = 10 };
        await Observe("support-nested-null-order", own, support, "Clients.ViewAssigned",
            r => new GetSupportsListByClientQuery.GetTracksListByClientQueryHandler(r).Handle(support, default),
            x =>
            {
                Assert.Equal(new[] { own.OlderSupport, own.NewerSupport }, x.Items.Select(y => y.SupportId));
                Assert.False(x.Items[0].IsActif);
                Assert.True(x.Items[0].HasNote);
                Assert.True(x.Items[1].IsActif);
                Assert.Null(x.Items[1].EndDate);
                Assert.Equal(own.Client, x.Items[1].Client.ClientId);
                Assert.True(x.Items[1].IsLastSupport);
            });
        var foreignSupport = new GetSupportsListByClientQuery { ClientId = other.Client, PageNumber = 1, PageSize = 10 };
        await Observe("support-foreign", own, foreignSupport, "Clients.ViewAll",
            r => new GetSupportsListByClientQuery.GetTracksListByClientQueryHandler(r).Handle(foreignSupport, default),
            x => { Assert.Empty(x.Items); Assert.Equal(0, x.TotalCount); });

        var clients = new GetClientsQuery();
        await Observe("client-list-null-email", other, clients, "Clients.ViewAll",
            r => new GetClientsQuery.GetClientsQueryHandler(r).Handle(clients, default),
            x => { var row = Assert.Single(x.Clients); Assert.Equal(other.Client, row.ClientId); Assert.Null(row.EmailAddress); });
        var search = new GetClientsBySearchTextQuery { SearchText = "synthetic-own" };
        await Observe("client-search", own, search, "Clients.ViewAssigned",
            r => new GetClientsBySearchTextQuery.GetClientsBySearchTextQueryHandler(r).Handle(search, default),
            x => Assert.Equal(own.Client, Assert.Single(x.Clients).ClientId));
        var detail = new GetClientDetailQuery { ClientId = own.Client };
        await Observe("client-detail", own, detail, "Clients.ViewAssigned",
            r => new GetClientDetailQuery.GetClientDetailQueryHandler(r).Handle(detail, default),
            x => { Assert.Equal(own.Client, x.ClientId); Assert.Equal("Test street", x.Address.Street); });
        var foreignDetail = new GetClientDetailQuery { ClientId = other.Client };
        await Observe("client-detail-foreign", own, foreignDetail, "Clients.ViewAll",
            r => new GetClientDetailQuery.GetClientDetailQueryHandler(r).Handle(foreignDetail, default),
            x => Assert.Null(x));
        var tracks = new GetClientsListQuery();
        await Observe("supports-list", own, tracks, "Clients.ViewAssigned",
            r => new GetClientsListQuery.GetSupportsListQueryHandler(r).Handle(tracks, default),
            x => Assert.Equal(new[] { own.OlderSupport, own.NewerSupport }, x.Tracks.Select(y => y.SupportId)));

        var currentAssessment = new GetCurrentAssessmentQuery { ClientId = own.Client };
        await Observe("assessment-current-nested", own, currentAssessment, "Clients.ViewAssigned",
            r => new GetCurrentAssessmentQuery.GetClientDetailQueryHandler(r).Handle(currentAssessment, default),
            x =>
            {
                Assert.False(x.IsFinalized);
                Assert.Equal(2, x.BilanProfessions.Count());
                Assert.Contains(x.BilanProfessions, y => y.ProfessionTitle == "Synthetic profession");
                Assert.Contains(x.BilanProfessions, y => y.ProfessionId is null && y.ProfessionTitle is null);
            });
        var lastAssessment = new GetLastAssessmentQuery { ClientId = own.Client };
        await Observe("assessment-last", own, lastAssessment, "Clients.ViewAll",
            r => new GetLastAssessmentQuery.GetLastAssessmentHandler(r).Handle(lastAssessment, default),
            x => Assert.True(x.IsFinalized));
        var assessments = new GetAssessmentsListQuery { ClientId = own.Client, PageNumber = 2, PageSize = 1 };
        await Observe("assessment-page", own, assessments, "Clients.ViewAll",
            r => new GetAssessmentsListQuery.GetBilansListQueryHandler(r).Handle(assessments, default),
            x => { Assert.Equal(2, x.TotalCount); Assert.Single(x.Items); });
        var archived = new GetArchivedBilansListQuery { PageNumber = 1, PageSize = 10 };
        await Observe("assessment-archived", own, archived, "Clients.ViewAssigned",
            r => new GetArchivedBilansListQuery.GetArchivedBilansListQueryHandler(r).Handle(archived, default),
            x => Assert.True(Assert.Single(x.Items).IsFinalized));

        foreach (var suffix in new[] { "empty", "partial" })
        {
            var sparse = await SeedAsync(suffix, suffix == "empty" ? 685 : 686);
            var sparseDetail = new GetClientDetailQuery { ClientId = sparse.Client };
            await Observe("client-detail-" + suffix, sparse, sparseDetail, "Clients.ViewAll",
                r => new GetClientDetailQuery.GetClientDetailQueryHandler(r).Handle(sparseDetail, default),
                x =>
                {
                    Assert.Equal(sparse.Client, x.ClientId);
                    Assert.Null(x.Email);
                    Assert.Null(x.Phone);
                    Assert.Null(x.MobilePhone);
                    Assert.Null(x.NativeLanguage);
                    Assert.Null(x.ContactLanguage);
                    Assert.Equal("", x.SupportStaffMemberName);
                    Assert.Equal("", x.SupportStaffMemberService);
                    Assert.Null(x.SupportStartDate);
                    Assert.Null(x.SupportEndDate);
                    Assert.Null(x.LastTrainingName);
                    Assert.Null(x.LastTrainingNote);
                    Assert.Null(x.LastTrainingResult);
                    Assert.Null(x.LastTrainingStartDate);
                    Assert.Null(x.LastTrainingEndDate);
                    if (suffix == "empty")
                    {
                        Assert.Null(x.Address);
                    }
                    else
                    {
                        Assert.Null(x.Address.Number);
                        Assert.Equal("Test partial street", x.Address.Street);
                        Assert.Null(x.Address.PostalCode);
                        Assert.Null(x.Address.City);
                        Assert.Null(x.Address.Country);
                    }
                });
        }

        reads.Commands.Clear();
        materializations.Types.Clear();
        var schoolNameSort = new GetSchoolRegistrationsListQuery
        {
            ClientId = own.Client, Filter = "", PageNumber = 1, PageSize = 10, Orderby = "BenficiaryName"
        };
        var sortError = await Assert.ThrowsAsync<InvalidOperationException>(() => Query(own, schoolNameSort,
            "Clients.ViewAll", r => new GetSchoolRegistrationsListQuery.GetSchoolRegistrationsListQueryHandler(
                r, new SortHelper<SchoolRegistrationDto>()).Handle(schoolNameSort, default)));
        Assert.Single(reads.Commands);
        Assert.Empty(materializations.Types);
        WriteEvidence("school-beneficiary-name-sort", sortError.GetType().Name, "null");

        reads.Commands.Clear();
        await Assert.ThrowsAsync<TenantAccessException>(() => Query(own, listActions, "Clients.ViewAll",
            r => new GetMonitoringActionsQueryHandler(r.MonitoringAction).Handle(listActions, default)));
        Assert.Empty(reads.Commands);
        await Assert.ThrowsAsync<TenantAccessException>(() => Query(own, support, "ReferenceData.View",
            r => new GetSupportsListByClientQuery.GetTracksListByClientQueryHandler(r).Handle(support, default)));
        Assert.Empty(reads.Commands);
        Assert.Empty(failures);

        async Task Observe<T>(string id, Seed seed, object request, string grant,
            Func<RepositoryManager, Task<T>> handle, Action<T> assert)
        {
            reads.Commands.Clear();
            materializations.Types.Clear();
            try
            {
                var result = await Query(seed, request, grant, handle);
                assert(result);
                Assert.NotEmpty(reads.Commands);
                Assert.Empty(materializations.Types);
                Assert.All(reads.Commands, command => Assert.StartsWith("SELECT", command.Sql.TrimStart(), StringComparison.OrdinalIgnoreCase));
                WriteEvidence(id, "PASS", JsonConvert.SerializeObject(result, Json));
            }
            catch (Exception error)
            {
                var status = error is PostgresException postgres ? error.GetType().Name + "/" + postgres.SqlState : error.GetType().Name;
                failures.Add(id + "/" + status);
                WriteEvidence(id, status, "null");
                output.WriteLine("ZEK39_DIAGNOSTIC|" + id + "|" + error.Message.ReplaceLineEndings(" "));
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("synthetic-ibis")]
    public async Task Dormant_ibis_returns_the_patch_without_a_matching_entity_member(string? value)
    {
        var sequence = value is null ? 695 : 696;
        var seed = await SeedAsync("ibis", sequence);
        var niss = SyntheticClient.Niss(sequence: sequence);
        var patch = new JsonPatchDocument<UpdateClientDto>();
        patch.Replace(x => x.IbisNumber, value);
        var request = new UpSertIbisNumberCommand { Niss = niss, PatchDoc = new ClientPatch(patch) };

        reads.Commands.Clear();
        var patched = await Query(seed, request, "Clients.EditAll", async repository =>
        {
            var before = await repository.Client.GetClientByNissAsync(niss);
            var handler = new UpSertIbisNumberCommand.UpSertIbisNumberCommandHandler(repository);
            var result = await handler.Handle(request, default);
            var after = await repository.Client.GetClientByNissAsync(niss);

            Assert.Equal(before.Id, after.Id);
            Assert.Equal(before.Ssn, after.Ssn);
            Assert.Equal(before.ReferenceNumber, after.ReferenceNumber);
            Assert.Equal(before.FullName, after.FullName);
            return result;
        }, readOnly: false);
        Assert.Equal(value, patched.IbisNumber);
        Assert.Null(typeof(Client).GetProperty(nameof(UpdateClientDto.IbisNumber)));
        Assert.Contains(reads.Commands, command => command.Sql.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase));
    }

    private async Task<T> Query<T>(Seed seed, object request, string grant, Func<RepositoryManager, Task<T>> handle, bool readOnly = true)
    {
        var tenant = new TenantContextScope();
        var operation = new TenantOperation(new Access(seed, grant), new Identity(seed.Organisation), tenant, tenant);
        await operation.AuthorizeAsync(request.GetType().GetCustomAttribute<RequiresTenantPermissionAttribute>()!, default);

        await using var connection = new NpgsqlConnection(fixture.RuntimeConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        if (readOnly)
        {
            await using var command = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, tx);
            await command.ExecuteNonQueryAsync();
        }
        await SetTenant(connection, tx, seed.Organisation);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection)
            .AddInterceptors(reads, materializations).Options;
        await using var db = new ApplicationDbContext(options, tenant, operation);
        await db.Database.UseTransactionAsync(tx);
        return await handle(new RepositoryManager(db));
    }

    private async Task<Seed> SeedAsync(string suffix, int sequence)
    {
        var organisation = Guid.NewGuid();
        var membership = Guid.NewGuid();
        var client = SyntheticClient.Create(SyntheticClient.Niss(sequence: sequence));
        client.ReferenceNumber = "synthetic-" + suffix;
        if (suffix == "other") client.Email = null;
        var worker = new SocialWorker();
        worker.ApplyProjection(membership, 1, Guid.NewGuid(), true, "Synthetic", "Worker",
            membership.ToString("N"), "Team", "T");
        var action = new MonitoringAction("Synthetic " + suffix + " action");
        var older = Report(new DateTime(2026, 1, 15), "Synthetic prior");
        var newer = Report(new DateTime(2026, 4, 15), "Synthetic current");
        var deleted = Report(new DateTime(2026, 7, 15), "Synthetic deleted");
        deleted.Softdelete = true;
        var previous = new SocialCase(client, DateTime.Today.AddDays(-8), worker, "Synthetic note")
            { EndDate = DateTime.Today.AddDays(-5), ReasonOfClosure = "Synthetic closure" };
        var current = new SocialCase(client, DateTime.Today.AddDays(-4), worker, null);
        var training = new Training("Synthetic " + suffix + " training", new TrainingField("Synthetic " + suffix + " field"));
        var school = new School("Synthetic " + suffix + " school", "Synthetic locality");
        var type = new TrainingType("Synthetic " + suffix + " type");
        var priorSchool = Registration(new DateTime(2026, 1, 1), "Synthetic note");
        var nextSchool = Registration(new DateTime(2026, 3, 1), null);

        await using var connection = new NpgsqlConnection(fixture.AdministratorConnectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync();
        await SetTenant(connection, tx, organisation);
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(connection).Options;
        await using var db = new ApplicationDbContext(options, TenantEnforcementTests.Scope(organisation));
        await db.Database.UseTransactionAsync(tx);
        if (suffix is "empty" or "partial")
        {
            client.Email = null;
            client.Phone = null;
            client.MobilePhone = null;
            client.NativeLanguage = null;
            client.ContactLanguage = null;
            client.Address = suffix == "empty"
                ? null
                : new ClientManagement.Core.ValueObjects.Address(null!, "Test partial street", null!, null!, null!);
            db.Add(client);
            await db.SaveChangesAsync();
            await tx.CommitAsync();
            return new(organisation, membership, client.Id, 0, 0, 0, 0, 0, 0, 0, 0);
        }
        db.AddRange(older, newer, deleted, previous, current, priorSchool, nextSchool);
        var assessment = NewAssessment(false);
        var archivedAssessment = NewAssessment(true);
        db.AddRange(assessment, archivedAssessment);
        db.Entry(assessment).Reference(x => x.Client).CurrentValue = client;
        db.Entry(archivedAssessment).Reference(x => x.Client).CurrentValue = client;
        db.AddRange(
            new ProfessionalAssessment(assessment, new Profession("Synthetic profession"), "Knowledge",
                "Behaviour", "Skill", "Knowledge to develop", "Behaviour to develop", "Skill to develop"),
            new ProfessionalAssessment(assessment, null!, "Knowledge", "Behaviour", "Skill",
                "Knowledge to develop", "Behaviour to develop", "Skill to develop"));
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new(organisation, membership, client.Id, worker.Id, action.Id, older.Id, newer.Id, deleted.Id,
            previous.Id, current.Id, nextSchool.Id);

        MonitoringReport Report(DateTime date, string comment) => new()
        {
            Client = client, SocialWorker = worker, MonitoringAction = action, ActionDate = date, ActionComment = comment
        };
        SchoolRegistration Registration(DateTime date, string? note) =>
            new(training, school, type, client, Level.A1, SchoolResult.Success, date, date.AddDays(30), note);
    }

    private static Assessment NewAssessment(bool finalized)
    {
        var assessment = new Assessment { IsFinalized = finalized };
        foreach (var property in typeof(Assessment).GetProperties()
            .Where(property => property.PropertyType == typeof(string) && property.CanWrite))
            property.SetValue(assessment, "Synthetic assessment");
        return assessment;
    }

    private static async Task SetTenant(NpgsqlConnection connection, NpgsqlTransaction tx, Guid organisation)
    {
        await using var command = new NpgsqlCommand("SELECT set_config('zeka.organisation_id', @org, true)", connection, tx);
        command.Parameters.AddWithValue("org", organisation.ToString("D"));
        await command.ExecuteNonQueryAsync();
    }

    private void WriteEvidence(string id, string status, string value)
    {
        var data = JsonConvert.SerializeObject(new
        {
            id, status, wire = Newtonsoft.Json.Linq.JToken.Parse(value),
            sql = reads.Commands.ToArray(), materializedEntities = materializations.Types.Order().ToArray()
        });
        output.WriteLine("ZEK39_RESULT|" + Convert.ToBase64String(Encoding.UTF8.GetBytes(data)));
    }

    private sealed record Seed(Guid Organisation, Guid Membership, int Client, int Worker, int Action,
        int OlderReport, int NewerReport, int DeletedReport, int OlderSupport, int NewerSupport, int NewerSchool);

    private sealed record Identity(Guid SelectedOrganisationId) : IOperationIdentity
    {
        public string SubjectId => "synthetic-projection-reader";
    }

    private sealed class Access(Seed seed, string grant) : ICurrentTenantAccess
    {
        public Task<TenantAccessDecision> ResolveAsync(string subject, Guid selected, CancellationToken cancellationToken) =>
            Task.FromResult(selected == seed.Organisation
                ? new TenantAccessDecision(TenantAccessOutcome.Authorized,
                    new(seed.Organisation, seed.Membership, new[] { grant }, "synthetic-projection", DateTimeOffset.UtcNow))
                : new TenantAccessDecision(TenantAccessOutcome.Denied));
    }

    private sealed class ReadCommands : DbCommandInterceptor
    {
        public List<SqlShape> Commands { get; } = new();

        public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            Commands.Add(new(command.CommandText, command.Parameters.Count));
            return result;
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(new(command.CommandText, command.Parameters.Count));
            return ValueTask.FromResult(result);
        }
    }

    private sealed record SqlShape(string Sql, int ParameterCount);

    private sealed class EntityMaterializations : IMaterializationInterceptor
    {
        public HashSet<string> Types { get; } = new(StringComparer.Ordinal);

        public object InitializedInstance(MaterializationInterceptionData data, object entity)
        {
            Types.Add(data.EntityType.ClrType.Name);
            return entity;
        }
    }
}
