using Zeka.Extensions.MultiTenancy.Abstractions;

namespace ClientManagement.Core.Common
{
    public abstract class Entity
    {
        public virtual int Id { get; protected set; }
        public string CreatedBy { get; set; } = String.Empty;
        public DateTime Created { get; set; }
        public string LastModifiedBy { get; set; } = String.Empty;
        public DateTime? LastModified { get; set; }
        public Boolean Softdelete { get; set; } = false;
        [System.ComponentModel.DataAnnotations.Schema.NotMapped]
        public string TenantName { get; set; } = string.Empty;
    }

    public abstract class TenantOwnedEntity : Entity, ITenantOwnedEntity
    {
        public Guid OrganisationId { get; private set; }

        public void AssignToOrganisation(Guid organisationId)
        {
            if (organisationId == Guid.Empty)
                throw new ArgumentException("Organisation ID cannot be empty.", nameof(organisationId));
            if (OrganisationId != Guid.Empty && OrganisationId != organisationId)
                throw new InvalidOperationException("Organisation ID is immutable once assigned.");

            OrganisationId = organisationId;
        }
    }
}
