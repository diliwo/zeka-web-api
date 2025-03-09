using ClientManagement.Core.Entities;
using ClientManagement.Core.Interfaces;
using ClientManagement.Infrastructure.Persistence.Helpers;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace ClientManagement.Infrastructure.Services;

public class TenantService : ITenantService
{
    private readonly TenantSettings _tenantSettings;
    private HttpContext _httpContext;
    private Tenant _tenant;
    public TenantService(IOptions<TenantSettings> tenantSettings, IHttpContextAccessor contextAccessor)
    {
        _tenantSettings = tenantSettings.Value;
        _httpContext = contextAccessor.HttpContext!;
        if (_httpContext != null)
        {
            //We add the tenant value in the request header
            if (_httpContext.Request.Headers.TryGetValue("tenant", out var tenantId))
            {
                SetTenant(tenantId!);
            }
            else
            {
                throw new Exception("Invalid Tenant!");
            }
        }
    }
    private void SetTenant(string tenantId)
    {
        _tenant = _tenantSettings!.Tenants.Where(a => a.TenantName == tenantId).FirstOrDefault();
        if (_tenant == null) throw new Exception("Invalid Tenant!");
        if (string.IsNullOrEmpty(_tenant.ConnectionString)) SetDefaultConnectionStringToCurrentTenant();
    }
    private void SetDefaultConnectionStringToCurrentTenant() =>
        _tenant.ConnectionString = _tenantSettings.DefaultConnectionString;
    public string GetConnectionString() => _tenant?.ConnectionString!;
    public Tenant GetTenant() => _tenant;
}