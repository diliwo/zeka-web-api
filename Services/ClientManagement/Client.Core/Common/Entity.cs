using Zeka.Extensions.MultiTenant;

namespace ClientManagement.Core.Common
{
    public abstract class Entity : IHasTenant
    {
        public virtual int Id { get; protected set; }
        public string CreatedBy { get; set; } = String.Empty;
        public DateTime Created { get; set; }
        public string LastModifiedBy { get; set; } = String.Empty;
        public DateTime? LastModified { get; set; }
        public Boolean Softdelete { get; set; } = false;
        public string TenantName { get; set; }
    }
}