namespace Infrastructure.IntegrationTests;

public sealed class AuthenticationStartupTests : Zeka.Authentication.Tests.RealHostStartupTests
{
    protected override string ApiDirectory => "Services/AuthManager/AuthManager.API";
    protected override string AssemblyName => "AuthManager.API";
    protected override bool IsIssuer => true;
}
