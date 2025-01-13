using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces
{
    public interface INatureOfContractRepository
    {
        void Persist(NatureOfContract type);
        NatureOfContract Get(int typeId, bool trackChanges = false);
        Task<NatureOfContract> GetAsync(int typeId, bool trackChanges = false);
        IQueryable<NatureOfContract> GetContracts(string filter = "");
        void SoftDelete(NatureOfContract contract);
    }
}