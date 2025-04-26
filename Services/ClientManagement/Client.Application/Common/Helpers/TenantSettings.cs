using ClientManagement.Core.Entities;

namespace ClientManagement.Application.Common.Helpers;

public class TenantSettings
{
    public string? DefaultConnectionString { get; set; }
    public List<Tenant>? Tenants { get; set; }
}