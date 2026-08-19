using AuthManager.Application.Common.Interfaces;

namespace AuthManager.Application.Common.Services
{
    public class ServiceManager(IAuthenticationService authenticationService) : IServiceManager
    {
        public IAuthenticationService AuthenticationService { get; } = authenticationService;
    }
}
