namespace ClientManagement.Core.Interfaces
{
    public interface IStaffMemberRepository
    {
        void Persist(StaffMember StaffMember);
        StaffMember Get(int id);
        bool UserAlreadyExists(string username, int idService);
        IQueryable<StaffMember> GetStaffMembers(string filter = null, string orderBy = "");
        //void Delete(StaffMember StaffMember);
        void SoftDelete(StaffMember StaffMember);
        public Task<bool> ServiceHasStaffMembers(int serviceId);
    }
}
