using Client.Core.Entities;

namespace Client.Core.Interfaces
{
    public interface IStaffRepository
    {
        void Persist(Staff staff);
        Staff Get(int id);
        bool UserAlreadyExists(string username, int idService);
        IQueryable<Staff> GetStaffs(string filter = null, string orderBy = "");
        //void Delete(Staff staff);
        void SoftDelete(Staff staff);
        public Task<bool> ServiceHasStaffs(int serviceId);
    }
}
