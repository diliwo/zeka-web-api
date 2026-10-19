using Xunit.Abstractions;

namespace Infrastructure.IntegrationTests;

/// <summary>
/// Separate diagnostic prerequisite, not PR93 acceptance. The same single test is
/// re-entered in fresh, credential-free vstest children; only the supervisor owns trust.
/// Never execute this test on an operator's Windows account. Discovery is safe.
/// </summary>
public sealed class GatewayHandlerTrustProbeTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Real_pool_default_validation_fixed_package()
    {
        try
        {
            if (Environment.GetEnvironmentVariable(GatewayHandlerTrustProbeSupport.ChildMode) is not null)
                await GatewayHandlerTrustProbeSupport.RunChildAsync();
            else
                await GatewayHandlerTrustProbeSupport.RunSupervisorAsync(output);
        }
        catch (Exception error)
        {
            // No exception message/inner exception: transport errors can contain targets.
            // Safe detailed receipts are written by the producer, never raw stderr/logs.
            throw new Xunit.Sdk.XunitException("Trust probe failed; inspect sanitized receipts. Error class: " + error.GetType().Name);
        }
    }
}
