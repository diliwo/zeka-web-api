using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces
{
    public interface IStaffMemberRepository
    {
        void Persist(SocialWorker StaffMember);
        SocialWorker Get(int id);
        bool UserAlreadyExists(string username, int idService);
        IQueryable<SocialWorker> GetStaffMembers(string filter = null, string orderBy = "");
        //void Delete(StaffMember StaffMember);
        void SoftDelete(SocialWorker StaffMember);
        public Task<bool> ServiceHasStaffMembers(int serviceId);
    }
}
