using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces
{
    public interface IProfessionRepository
    {
        void Persist(Profession profession);
        Profession Get(int id);
        Task<Profession> GetASync(int professionId);
        IQueryable<Profession> GetProfessions(string filter, string orderBy = "");
        void SoftDelete(Profession service);
        public Boolean IsProfessionUnique(string name);
    }
}
