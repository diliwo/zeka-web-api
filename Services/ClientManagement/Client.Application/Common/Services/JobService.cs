using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Core.Common.Dto;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Enums;
using ClientManagement.Core.Interfaces;

namespace ClientManagement.Application.Common.Services
{
    public class JobService : IJobService
    {
        private readonly IRepositoryManager _repository;

        public JobService(
            IRepositoryManager repository
            )
        {
            _repository = repository;
        }

        public void SoftDeleteCandidacy(Candidacy candidacy)
        {
            var jobOffer = _repository.JobOffer.GetAsync(candidacy.JobOfferId).Result;

            if (jobOffer != null)
            {
                var job = _repository.Job.GetAsync(jobOffer.JobId).Result;
                
                if (job != null)
                {
                    var listOfWonJobOffers = _repository.JobOffer.GetJobOffersByJobId(job.Id).OrderBy(j => j.Id).ToList();

                    var lastWonjobOffer = listOfWonJobOffers.Count > 0 ?  listOfWonJobOffers[listOfWonJobOffers.Count - 1] : null;

                    jobOffer.StatusOfJobOffer = StatusOfJobOffer.Open;

                    if (lastWonjobOffer != null && lastWonjobOffer.Id == jobOffer.Id && candidacy.IsHired == true)
                    {
                        var secondLast = listOfWonJobOffers.Count > 1 ? listOfWonJobOffers[listOfWonJobOffers.Count - 2] : null;

                        Candidacy precedentEngagement = new Candidacy();

                        if (secondLast!= null)
                        {
                            precedentEngagement = _repository.Candidacy.GetWonCandidacyByJobOfferId(secondLast.Id);
                        }
                        
                        if (precedentEngagement != null && precedentEngagement.Id != default(int))
                        {
                            job.OccupiedFrom = secondLast.StartOccupationDate;
                            job.OccupiedTo = secondLast.EndOccupationDate;
                            job.OccupiedJobOfferId = secondLast.Id;
                            var client = _repository.Client.Get(precedentEngagement.BeneficiaryId);

                            job.OccupiedBy = $"{client.FullName}";

                            jobOffer.StartOccupationDate = secondLast.StartOccupationDate;
                            jobOffer.EndOccupationDate = secondLast.StartOccupationDate;

                        }
                        else
                        {
                            jobOffer.StartOccupationDate = null;
                            jobOffer.EndOccupationDate = null;
                            job.OccupiedTo = null;
                            job.OccupiedBy = null;
                            job.OccupiedFrom = null;
                            job.OccupiedJobOfferId = null;
                        }

                        _repository.Job.Persist(job);
                    }
                    _repository.JobOffer.Persist(jobOffer);
                }
            }

            candidacy.Softdelete = true;
            _repository.Candidacy.SoftDelete(candidacy);
            _repository.SaveAsync();
        }

        public IQueryable<JobOccupantDto> GetJobOccupants(int jobId, string filter)
        {
            var query = _repository.JobOffer.GetJobOffersByJobId(jobId)
                .Where(offer => offer.StartOccupationDate != null && offer.EndOccupationDate != null && offer.Candidacies.Any(x => x.IsHired)).Select( occupant => new JobOccupantDto()
                {
                    FirstName = occupant.Candidacies.SingleOrDefault(c => c.IsHired).Beneficiary.FirstName,
                    LastName = occupant.Candidacies.SingleOrDefault(c => c.IsHired).Beneficiary.LastName,
                    ReferenceNumber = occupant.Candidacies.SingleOrDefault(c => c.IsHired).Beneficiary.ReferenceNumber,
                    StartOccupationDate = occupant.StartOccupationDate,
                    EndOccupationDate = occupant.EndOccupationDate
                });

            if (!string.IsNullOrEmpty(filter))
            {
                var predicate = PredicateBuilder.New<JobOccupantDto>();

                predicate = predicate.Or(p => p.FirstName.ToLower().Contains(filter.ToLower().Trim()));
                predicate = predicate.Or(p => p.LastName.ToLower().Contains(filter.ToLower().Trim()));
                predicate = predicate.Or(p => p.ReferenceNumber.ToLower().Contains(filter.ToLower().Trim()));

                query = query.Where(predicate);
            }

            return query;
        }

        public async Task<List<EngagementWithAnnualClosure>> GetOccupiedJobsWithClosureWithen60Days()
        {
            return await _repository.Job.GetOccupiedJobsWithClosureWithen60Days();
        }

        public async Task<int> UpdateIntegrationEmployment(Job job, DateTime occupiedFrom, DateTime? occupiedTo, int employmentTerminationTypeId = default, IList<int> employmentTerminationReasonIds = null)
        {

            if (job is null)
            {
                throw new NotFoundException(nameof(job));
            }

            JobOffer jobOffer = await _repository.JobOffer.GetAsync((int)job.OccupiedJobOfferId);

            if (jobOffer == null)
            {
                throw new NotFoundException(nameof(jobOffer));
            }

            if (occupiedFrom != DateTime.MinValue)
            {
                job.OccupiedFrom = occupiedFrom;
                jobOffer.StartOccupationDate = occupiedFrom.ToLocalTime();
            }


            if (occupiedTo.HasValue && DateTime.Compare((DateTime)occupiedTo, occupiedFrom) >= 0)
            {
                job.OccupiedTo = occupiedTo;
                jobOffer.EndOccupationDate = occupiedTo.Value.ToLocalTime();

                employmentTerminationReasonIds ??= new List<int>();

                await _repository.TerminationReasonForEmploymentRepository.RemoveByIdJobOfferAsync(
                    (int)job.OccupiedJobOfferId);

                foreach (var id in employmentTerminationReasonIds)
                {
                    var reason = await _repository.EmploymentTerminationReasonRepository.GetByAsync(id);

                    if (reason is not null)
                    {
                        jobOffer.AddTerminationReason(jobOffer, reason);
                    }
                }

                if (employmentTerminationTypeId != default(int))
                {
                    var type = await _repository.EmploymentTerminationTypeRepository.GetAsync(employmentTerminationTypeId);

                    if (type is not null)
                    {
                        jobOffer.EmploymentTerminationType = type;
                    }
                }
            }

            _repository.Job.Persist(job);
            _repository.JobOffer.Persist(jobOffer);

            _repository.Save();

            return job.Id;
        }
    }
}