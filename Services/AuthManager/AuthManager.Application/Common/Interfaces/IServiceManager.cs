namespace AuthManager.Application.Common.Interfaces;

public interface IServiceManager
{
	IAuthenticationService AuthenticationService { get; }
}
