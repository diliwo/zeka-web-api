using DiliBeneficiary.Core.Common.Dto;
using DiliBeneficiary.Core.Entities;

namespace DiliBeneficiary.Core.Interfaces
{
    public interface ISupportRepository
    {
        void Persist(Support support);
        Support Get(int id);
        Task<Support> GetAsync(int id);
        Support GetLastSupportForBeneficiary(int id);
        Task<Support> GetLastSupportForBeneficiaryAsync(int id);
        IQueryable<Support> GetSupports();
        IQueryable GetSupportsByBeneficiaryId(int id);
        //void Delete(Support support);
        IEnumerable<Support> GetSupportsByBeneficiary(int id);
        bool GetNumberOfBeneficiarySupports(int id);
        void SoftDelete(Support support);
        public Task<bool> DateAlreadyExists(int beneficiaryId, DateTime date);
        public Task<bool> DateIsEarlierThanExistingDates(int beneficiaryId, DateTime date);
        public Task<bool> DateIsEarlierThanExistingDates(int beneficiaryId, DateTime date, int supportId);
        public Task<bool> EndDateIsGreaterThanStartDate(int? supportId, DateTime? endDate);
        public bool isSupportForBeneficiary(int beneficiaryId, int? supportId);
        IQueryable<MyConsultantSupportDto> GetConsultantSupportsByUserName(string username, string filter="", bool isActive = true);
        public IQueryable<MyJobCoachSupportDto> GetJobCoachSupportsByUserName(string username, string filter = "", bool isActive = true);
        Support GetWithDetails(int id);
    }
}
