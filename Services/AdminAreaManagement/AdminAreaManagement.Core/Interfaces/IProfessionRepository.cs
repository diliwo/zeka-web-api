using AdminAreaManagement.Core.Entities;

namespace AdminAreaManagement.Core.Interfaces
{
    public interface IProfessionRepository
    {
        void Persist(Profession profession);
        Profession Get(int id);
        Task<Profession> GetASync(int professionId);
        IQueryable<Profession> GetProfessions(string filter, string orderBy = "");
        Task SoftDelete(Profession service);
        public Boolean IsProfessionUnique(string name);
    }
}
