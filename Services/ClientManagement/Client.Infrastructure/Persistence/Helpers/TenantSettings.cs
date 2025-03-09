using ClientManagement.Core.Entities;

namespace ClientManagement.Infrastructure.Persistence.Helpers;

public class TenantSettings
{
    public string? DefaultConnectionString { get; set; }
    public List<Tenant>? Tenants { get; set; }
}