using Zeka.Extensions.MultiTenant;

namespace AdminAreaManagement.Core.Common
{
    public abstract class Entity : IHasTenant
    {
        public int Id { get; set; }
        public string CreatedBy { get; set; } = String.Empty;
        public DateTime Created { get; set; }
        public string LastModifiedBy { get; set; } = String.Empty;
        public DateTime? LastModified { get; set; }
        public Boolean Softdelete { get; set; } = false;
        public string TenantName { get; set; }
    }
}
