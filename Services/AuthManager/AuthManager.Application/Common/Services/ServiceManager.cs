using AuthManager.Application.Common.Interfaces;
using AuthManager.Core.Models.Users;
using AutoMapper;
using Microsoft.AspNetCore.Identity;

namespace AuthManager.Application.Common.Services
{
    public class ServiceManager(IMapper mapper, UserManager<User> userManager, IJwtService jwtService) : IServiceManager
    {
        private readonly Lazy<IAuthenticationService> _authenticationService = new(()
            => new AuthenticationService(userManager, mapper, jwtService));

        public IAuthenticationService AuthenticationService => _authenticationService.Value;
    }
}
