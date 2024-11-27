using Client.Core.Interfaces;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Client.API.Services
{
    public class CurrentUserService : ICurrentUserService
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public CurrentUserService(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }
        //var StaffUserName = _httpContextAccessor.HttpContext.User.Identity.Name;
        public string UserId => _httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        public string Username => _httpContextAccessor.HttpContext?.User?.Identity.Name;
    }
}
