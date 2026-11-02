using Zeka.Extensions.MultiTenancy.Abstractions;
using System.Net;
using ClientManagement.Infrastructure.Authorization;
using ClientManagement.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Zeka.Contracts.Staff.V1;
using Zeka.WorkerCredentials;
using Zeka.WorkerCredentials.Testing;

namespace Infrastructure.IntegrationTests;

public sealed class WorkerCredentialBoundaryTests
{
    [Fact]
    public async Task Missing_package_fails_before_scope_or_database_and_emits_no_adoption()
    {
        var credentials = new RefusingCredentials();
        await using var services = new ServiceCollection().BuildServiceProvider();
        var consumer = new StaffProjectionConsumer(services.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationManager(), credentials);
        await Assert.ThrowsAsync<WorkerCredentialUnavailableException>(() => consumer.Handle(Message(Guid.NewGuid())));
        Assert.Equal(1, credentials.Reads);
        Assert.Equal(0, credentials.Successes);
    }

    [Fact]
    public async Task Out_of_scope_delivery_fails_before_opening_operation_scope()
    {
        var allowed = Guid.NewGuid();
        var credentials = new ObservedCredentials(WorkerCredentialFixture.For([allowed], WorkerCredentialEncoding.ClientProfile));
        await using var services = new ServiceCollection().BuildServiceProvider();
        var consumer = new StaffProjectionConsumer(services.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationManager(), credentials);
        await Assert.ThrowsAsync<ClientManagement.Application.Common.Authorization.TenantAccessException>(() =>
            consumer.Handle(Message(Guid.NewGuid())));
        Assert.Equal(1, credentials.Reads);
        Assert.Equal(0, credentials.Successes);
    }

    [Fact]
    public async Task Real_tenant_authorization_denial_prevents_database_resolution_and_adoption()
    {
        var organisation = Guid.NewGuid();
        var credentials = new ObservedCredentials(WorkerCredentialFixture.For([organisation], WorkerCredentialEncoding.ClientProfile));
        var http = new Denied();
        var services = new ServiceCollection().AddScoped<TenantContextScope>()
            .AddSingleton<IHttpClientFactory>(new Factory(http));
        await using var provider = services.BuildServiceProvider();
        var config = new ConfigurationManager { ["TenantAuthorization:AuthManagementUrl"] = WorkerCredentialFixture.Issuer };
        var consumer = new StaffProjectionConsumer(provider.GetRequiredService<IServiceScopeFactory>(), config, credentials);
        await Assert.ThrowsAsync<ClientManagement.Application.Common.Authorization.TenantAccessException>(() =>
            consumer.Handle(Message(organisation)));
        Assert.Equal(1, http.Calls);
        Assert.Equal(1, credentials.Reads);
        Assert.Equal(0, credentials.Successes);
    }

    private static StaffProjectionChangedV1 Message(Guid organisation) =>
        new(organisation, Guid.NewGuid(), 1, true, "Generated", "Fixture", "fixture", "Team", "T");
    private sealed class RefusingCredentials : IWorkerCredentialProvider
    {
        internal int Reads, Successes;
        public ValueTask<WorkerCredentialSnapshot> ReadAsync(CancellationToken cancellationToken)
        { Reads++; throw new WorkerCredentialUnavailableException("WORKER_PROTECTED_FILE_UNAVAILABLE"); }
        public void MarkSuccessfulUse(WorkerCredentialSnapshot snapshot) => Successes++;
    }
    private sealed class ObservedCredentials(IWorkerCredentialProvider inner) : IWorkerCredentialProvider
    {
        internal int Reads, Successes;
        public ValueTask<WorkerCredentialSnapshot> ReadAsync(CancellationToken cancellationToken)
        { Reads++; return inner.ReadAsync(cancellationToken); }
        public void MarkSuccessfulUse(WorkerCredentialSnapshot snapshot) { Successes++; inner.MarkSuccessfulUse(snapshot); }
    }
    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    { public HttpClient CreateClient(string name) => new(handler, disposeHandler: false); }
    private sealed class Denied : HttpMessageHandler
    {
        internal int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)); }
    }
}