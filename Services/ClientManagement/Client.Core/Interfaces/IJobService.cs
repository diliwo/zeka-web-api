using ClientManagement.Core.Common.Dto;
using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces
{
    public interface IJobService
    {
        void SoftDeleteCandidacy(Candidacy candidacy);
        IQueryable<JobOccupantDto> GetJobOccupants(int jobId, string filter);
        Task<List<EngagementWithAnnualClosure>> GetOccupiedJobsWithClosureWithen60Days();
        Task<int> UpdateIntegrationEmployment(Job job, DateTime occupiedFrom, DateTime? occupiedTo, int employmentTerminationTypeId = default, IList<int> employmentTerminationReasonIds = null);
    }
}