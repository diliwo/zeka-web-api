namespace Infrastructure.IntegrationTests;

public sealed class AuthenticationStartupTests : Zeka.Authentication.Tests.RealHostStartupTests
{
    protected override string ApiDirectory => "Services/ClientManagement/Client.API";
    protected override string AssemblyName => "ClientManagement.API";
}
