using DiliBeneficiary.Core.Entities;

namespace DiliBeneficiary.Core.Interfaces
{

    public interface IBeneficiaryRepository
    {
        void Persist(Beneficiary beneficiary);
        Beneficiary Get(int id, bool trackChanges = false);

        Task<Beneficiary> GetSourceIdAsync(int id, bool trackChanges = false);
        Task<Beneficiary> GetAsync(int id,bool trackChanges = false);
        Task<Beneficiary> GetWithApplicationsAsync(int id, bool trackChanges = false);
        Task<Beneficiary> GetWithApplicationsByNameAsync(string name, bool trackChanges = false);
        Beneficiary GetBeneficiaryByNiss(string niss, bool trackChanges = false);
        Task<Beneficiary> GetBeneficiaryByNissAsync(string niss, bool trackChanges = false);
        Task<List<string>> GetBeneficiaryNissesAsync(bool trackChanges = false);
        IQueryable<Beneficiary> GetBeneficiaries();
        Task<IEnumerable<Beneficiary>> GetBeneficiariesAsync(bool trackChanges);
        IQueryable<Beneficiary> GetBeneficiariesBySearchText(string text);
    }
}
