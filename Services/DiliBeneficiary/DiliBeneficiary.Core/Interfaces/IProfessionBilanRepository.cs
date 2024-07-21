using DiliBeneficiary.Core.Entities;

namespace DiliBeneficiary.Core.Interfaces
{
    public interface IProfessionBilanRepository
    {
        void Persist(BilanProfession bilanProfession);
        BilanProfession Get(int professionBilanId);
        Task<BilanProfession> GetASync(int professionBilanId);
        IQueryable<BilanProfession> GetProfessionBilans();
        public IQueryable GetProfessionByBilanId(int id);
        void SoftDelete(BilanProfession bilanProfession);
    }
}