using AdminAreaManagement.Core.Entities;

namespace AdminAreaManagement.Core.Interfaces
{
    public interface ITeamRepository
    {
        void Persist(Team team);
        Team Get(int id);
        Boolean IsTeamUnique(string name);
        IQueryable<Team> GetTeams(string filter);
        void SoftDelete(Team team);
        public Task<bool> ContainsStaffMembers(int teamId);
    }
}
