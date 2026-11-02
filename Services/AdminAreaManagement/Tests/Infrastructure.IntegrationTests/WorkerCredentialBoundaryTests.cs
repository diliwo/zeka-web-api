using Zeka.Extensions.MultiTenancy.Abstractions;
using System.Net;
using AdminAreaManagement.Infrastructure.Authorization;
using AdminAreaManagement.Infrastructure.Messaging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
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
        var worker = new StaffProjectionRetryWorker(services.GetRequiredService<IServiceScopeFactory>(),
            new ConfigurationManager(), NullLogger<StaffProjectionRetryWorker>.Instance, credentials);
        await Assert.ThrowsAsync<WorkerCredentialUnavailableException>(() => worker.DispatchCycleAsync(default));
        Assert.Equal(1, credentials.Reads);
        Assert.Equal(0, credentials.Successes);
    }

    [Fact]
    public async Task Real_tenant_authorization_denial_prevents_database_resolution_and_adoption()
    {
        var organisation = Guid.NewGuid();
        var credentials = new ObservedCredentials(WorkerCredentialFixture.For([organisation], WorkerCredentialEncoding.AdminAreaProfile));
        var http = new Denied();
        var services = new ServiceCollection().AddScoped<TenantContextScope>()
            .AddSingleton<IHttpClientFactory>(new Factory(http));
        await using var provider = services.BuildServiceProvider();
        var config = new ConfigurationManager
        {
            ["TenantAuthorization:AuthManagementUrl"] = WorkerCredentialFixture.Issuer,
            ["TenantWorker:OrganisationIds:0"] = organisation.ToString("D")
        };
        var worker = new StaffProjectionRetryWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            config, NullLogger<StaffProjectionRetryWorker>.Instance, credentials);
        Assert.True(await worker.DispatchCycleAsync(default));
        Assert.Equal(1, http.Calls);
        Assert.Equal(1, credentials.Reads);
        Assert.Equal(0, credentials.Successes);
    }

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