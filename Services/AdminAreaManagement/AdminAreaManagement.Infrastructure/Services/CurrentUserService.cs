using AdminAreaManagement.Core.Interfaces;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;

namespace AdminAreaManagement.Infrastructure.Services;

public class CurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }
    public string UserId => _httpContextAccessor.HttpContext?.User?.FindFirstValue(ClaimTypes.NameIdentifier);
    public string Username => _httpContextAccessor.HttpContext?.User?.Identity.Name;
}