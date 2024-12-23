using AdminAreaManagement.Core.Entities;

namespace AdminAreaManagement.Core.Interfaces
{
    public interface IStaffMemberRepository
    {
        void Persist(StaffMember StaffMember);
        StaffMember Get(int id);
        IQueryable<StaffMember> GetStaffMembers(string filter = null, string orderBy = "");
        void SoftDelete(StaffMember StaffMember);
        bool StaffMemberBelongsToTeam(string username, int idService);
    }
}
