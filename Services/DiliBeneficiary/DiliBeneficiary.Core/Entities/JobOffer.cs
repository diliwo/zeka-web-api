using DiliBeneficiary.Core.Common;
using DiliBeneficiary.Core.Enums;
using DiliBeneficiary.Core.Exceptions;

namespace DiliBeneficiary.Core.Entities
{
    public class JobOffer : Entity
    {
        public DateTime VacancyDate { get; set; }
        public DateTime? StartOccupationDate { get; set; }
        public DateTime? EndOccupationDate { get; set;  }
        public int JobId { get; set; }
        public Job Job { get; set; }

        public StatusOfJobOffer StatusOfJobOffer { get; set; } = StatusOfJobOffer.Open;
        public virtual ICollection<Candidacy> Candidacies { get; set; } = new List<Candidacy>();
        public IList<TerminationReasonForEmployment> TerminationReasonsForEmployment { get; set; } = new List<TerminationReasonForEmployment>();
        public EmploymentTerminationType EmploymentTerminationType { get; set; }
        public IEnumerable<String> Candidates
        {
            get => Candidacies.Select(j => j.Beneficiary?.FullName);
        }

        public JobOffer() {}

        public bool isInProgress
        {
            get
            {
                var today = DateTime.Today;
                if ((StartOccupationDate < today && EndOccupationDate > today) || StartOccupationDate < today && EndOccupationDate == null)
                {
                    return true;
                }
                return false;
            }
        }

        public JobOffer(Job job, DateTime vacancyDate)
        {
            if (vacancyDate == null)
            {
                throw new ArgumentNullException(nameof(vacancyDate));
            }

            Job = job ?? throw new ArgumentNullException(nameof(job));
            VacancyDate = vacancyDate;
        }

        public void AddCandidate(Beneficiary candidate, DateTime applicationDate)
        {
            if (Candidacies.Any(x => x.BeneficiaryId == candidate.Id))
            {
                throw new InvalidOperationException("Ce candidat a déjà postulé à cette offre d'emploi");
            }

            if (candidate == null)
            {
                throw new ArgumentNullException(nameof(candidate));
            }

            Candidacies.Add(new Candidacy(this,candidate,applicationDate));
        }

        public void HireCandidate(Beneficiary candidate)
        {
            if (Candidacies.Any(j => j.IsHired == true))
            {
                throw new AlreadyOccupiedException(Job.JobTitle);
            }
            else if (Candidacies.Any(c => c.BeneficiaryId != candidate.Id))
            {
                throw new CannotHireException(candidate.FirstName + " " + candidate.LastName);
            }

            Candidacies.Where(c => c.BeneficiaryId == candidate.Id).Single().IsHired = true;
        }

        public void AddTerminationReason(JobOffer offer, EmploymentTerminationReason reason)
        {
            TerminationReasonsForEmployment.Add(new TerminationReasonForEmployment(offer, reason));
        }
    }
}
