using DiliBeneficiary.Core.Common;
using DiliBeneficiary.Core.Enums;

namespace DiliBeneficiary.Core.Entities
{
    public class Candidacy : Entity
    {
        public int JobOfferId { get; set; }
        public virtual JobOffer JobOffer { get; set; }
        public int BeneficiaryId { get; set; }
        public virtual Beneficiary Beneficiary { get; set; }
        public Boolean IsHired { get; set; }
        public Boolean Softdelete { get; set; }
        public DateTime ApplicationDate { get; set; }
        public virtual ICollection<EmploymentStatusHistory> EmploymentStatusHistories { get; set; } = new List<EmploymentStatusHistory>();

        public Candidacy() { }

        public Candidacy(JobOffer jobOffer, Beneficiary beneficiary, DateTime applicationDate)
        {
            JobOffer = jobOffer ?? throw new ArgumentNullException(nameof(jobOffer));
            Beneficiary = beneficiary ?? throw new ArgumentNullException(nameof(beneficiary));
            ApplicationDate = applicationDate;
        }

        public void AddEmploymentStatus(DateTime statDate, EmploymentStatus status)
        {
            if (!EmploymentStatusHistories.Any(e => e.StartDate == statDate && e.EmploymentStatus == status && e.CandidacyId == this.Id))
            {
                EmploymentStatusHistories.Add(new EmploymentStatusHistory()
                {
                    EmploymentStatus = status,
                    StartDate = statDate,
                    Candidacy = this
                });
            }
        }

        public bool IsCurrentlyHired()
        {
            bool isCurrentlyHired = IsHired && JobOffer.StartOccupationDate <= DateTime.Now && 
                                    (JobOffer.EndOccupationDate is null || JobOffer.EndOccupationDate >= DateTime.Now);

            return isCurrentlyHired;
        }
    }
}