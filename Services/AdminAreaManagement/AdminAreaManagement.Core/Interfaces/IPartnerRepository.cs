using AdminAreaManagement.Core.Entities;

namespace AdminAreaManagement.Core.Interfaces
{

    public interface IPartnerRepository
    {
        void Persist(Partner partner);
        Partner Get(int id);
        IQueryable<Partner> GetPartners(string filter = null, bool onlyActive = false);
        void SoftDelete(Partner referent);
    }
}
