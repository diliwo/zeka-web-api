using System.Net;
using System.Net.Http.Json;
using ClientManagement.Application.Common.Authorization;
using ClientManagement.Infrastructure.Messaging;
using ClientManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zeka.Contracts.Staff.V1;
using Zeka.Extensions.MultiTenancy.Abstractions;

namespace Infrastructure.IntegrationTests;

public sealed class StaffProjectionTests(TenantDatabase fixture) : IClassFixture<TenantDatabase>
{
    [Fact]
    public async Task Consumer_uses_fresh_authorized_scopes_and_idempotent_versioned_membership_snapshots()
    {
        var a = Guid.NewGuid(); var b = Guid.NewGuid(); var membership = Guid.NewGuid();
        var otherMembership = Guid.NewGuid();
        var handler = new AccessHandler();
        handler.Memberships.Add(membership, a);
        handler.Memberships.Add(otherMembership, b);
        var config = new ConfigurationManager();
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TenantWorker:SubjectId"] = "worker", ["TenantWorker:BearerToken"] = "synthetic-test-token",
            ["TenantAuthorization:AuthManagementUrl"] = "https://auth.invalid/",
            ["ConnectionStrings:ClientApiConnection"] = fixture.ConnectionString
        });
        var services = new ServiceCollection();
        ClientManagement.Infrastructure.DependencyInjection.AddInfrastructure(services, config);
        services.AddSingleton<IHttpClientFactory>(new Factory(handler));
        await using var provider = services.BuildServiceProvider();
        var options = new DbContextOptionsBuilder<ApplicationDbContext>().UseNpgsql(fixture.ConnectionString).Options;
        var consumer = new StaffProjectionConsumer(provider.GetRequiredService<IServiceScopeFactory>(), config);
        var created = Message(a, membership, 1, true, "original");
        await consumer.Handle(created); await consumer.Handle(created);
        await using (var context = new ApplicationDbContext(options, TenantEnforcementTests.Scope(a)))
        {
            var worker = await context.SocialWorkers.SingleAsync();
            var client = ClientManagement.Tests.Common.SyntheticClient.Create(
                ClientManagement.Tests.Common.SyntheticClient.Niss(sequence: 81));
            context.Add(new ClientManagement.Core.Entities.SocialCase(client, DateTime.Today.AddDays(-1), worker));
            await context.SaveChangesAsync();
            Assert.Single(await context.AssignedClients(membership).ToListAsync());
        }
        handler.Inactive.Add(membership);
        await consumer.Handle(Message(a, membership, 3, false, "renamed"));
        await consumer.Handle(Message(a, membership, 2, true, "out-of-order"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => consumer.Handle(Message(a, membership, 3, false, "conflicting-revision")));
        await consumer.Handle(Message(b, otherMembership, 1, true, "other-organisation"));
        await Assert.ThrowsAsync<TenantAccessException>(() => consumer.Handle(Message(a, membership, 4, true, "inactive")));
        await Assert.ThrowsAsync<TenantAccessException>(() => consumer.Handle(Message(b, membership, 4, true, "cross-organisation")));
        await using (var context = new ApplicationDbContext(options, TenantEnforcementTests.Scope(a)))
        {
            var worker = await context.SocialWorkers.SingleAsync();
            Assert.True(worker.Softdelete); Assert.Equal("renamed", worker.UserName); Assert.Equal(3, worker.ProjectionVersion);
            Assert.Empty(await context.AssignedClients(membership).ToListAsync());
            Assert.Empty(await new SupportRepository(context).GetConsultantSupportsByMembership(membership).ToListAsync());
            context.Entry(worker).Property(x => x.OrganisationMembershipId).CurrentValue = Guid.NewGuid();
            await Assert.ThrowsAsync<InvalidOperationException>(() => context.SaveChangesAsync());
        }
        await using (var context = new ApplicationDbContext(options, TenantEnforcementTests.Scope(b)))
            Assert.Equal("other-organisation", (await context.SocialWorkers.SingleAsync()).UserName);
        handler.Allow = false;
        await Assert.ThrowsAsync<TenantAccessException>(() => consumer.Handle(Message(a, membership, 4, true, "forged")));
        await Assert.ThrowsAsync<TenantAccessException>(() => consumer.Handle(Message(Guid.Empty, membership, 4, true, "forged")));
    }
    private static StaffProjectionChangedV1 Message(Guid organisation, Guid membership, long revision, bool active, string name)
        => new(organisation, membership, revision, active, "Synthetic", "Worker", name, "Team", "T");
    private sealed class Factory(AccessHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }
    private sealed class AccessHandler : HttpMessageHandler
    {
        public bool Allow { get; set; } = true;
        public Dictionary<Guid, Guid> Memberships { get; } = new();
        public HashSet<Guid> Inactive { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("synthetic-test-token", request.Headers.Authorization?.Parameter);
            if (request.RequestUri!.AbsolutePath.Contains("/memberships/"))
            {
                var segments = request.RequestUri.Segments;
                var organisation = Guid.Parse(segments[4].TrimEnd('/'));
                var membership = Guid.Parse(segments.Last());
                return Task.FromResult(new HttpResponseMessage(Allow && Memberships.TryGetValue(membership, out var owner)
                    && owner == organisation && (!Inactive.Contains(membership)
                        || request.RequestUri.Query.Contains("includeInactive=True", StringComparison.OrdinalIgnoreCase))
                    ? HttpStatusCode.NoContent : HttpStatusCode.Forbidden));
            }
            return Task.FromResult(Allow ? new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { ContractVersion = 1, SubjectId = "worker",
                    OrganisationId = Guid.Parse(request.RequestUri!.Segments.Last()), OrganisationMembershipId = Guid.NewGuid(),
                    EffectivePermissionCodes = new[] { "TeamConfiguration.ManageStaffProfiles" }, DecisionVersion = "1", ObservedAtUtc = DateTimeOffset.UtcNow })
            } : new(HttpStatusCode.Forbidden));
        }
    }
}
