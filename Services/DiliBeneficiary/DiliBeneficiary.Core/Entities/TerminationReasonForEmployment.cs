using DiliBeneficiary.Core.Common;

namespace DiliBeneficiary.Core.Entities;

public class TerminationReasonForEmployment : Entity
{
    public int JobOfferId { get; set; }
    public virtual JobOffer JobOffer { get; set; }
    public int EmploymentTerminationReasonId { get; set; }
    public EmploymentTerminationReason EmploymentTerminationReason { get; set; }

    public TerminationReasonForEmployment(JobOffer jobOffer, EmploymentTerminationReason employmentTerminationReason)
    {
        JobOffer = jobOffer;
        EmploymentTerminationReason = employmentTerminationReason;
    }

    public TerminationReasonForEmployment(){}
}