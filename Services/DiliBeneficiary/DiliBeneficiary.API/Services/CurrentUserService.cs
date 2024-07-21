using DiliBeneficiary.Core.Interfaces;
using System.Security.Claims;

namespace DiliBeneficiary.API.Services
{
    public class CurrentUserService : ICurrentUserService
    {
        private readonly IHttpContextAccessor _httpContextAccessor;

        public CurrentUserService(IHttpContextAccessor httpContextAccessor)
        {
            _httpContextAccessor = httpContextAccessor;
        }
        //var referentUserName = _httpContextAccessor.HttpContext.User.Identity.Name;
        public string UserId => _httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        public string Username => _httpContextAccessor.HttpContext?.User?.Identity.Name;
    }
}
