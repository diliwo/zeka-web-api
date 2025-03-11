using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces;

public interface ITenantService
{
    public string GetConnectionString();
    public Tenant GetTenant();
}