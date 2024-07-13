using DiliBeneficiary.Core.Common;
using DiliBeneficiary.Core.Enums;

namespace DiliBeneficiary.Core.Entities
{
    public class Job : Entity
    {
        public String JobTitle { get; set; }
        public String JobNumber { get; set; }
        public String Reward { get; set; }
        public String? CommentReward { get; set; }
        public Boolean IsVacant { get; set; } = false;
        public String? Comment { get; set; }
        public TypeOfJob TypeOfJob { get; set; } = TypeOfJob.New;
        public StatusOfJobOffer StatusOfJobOffer { get; set; } = StatusOfJobOffer.Open;
        public CategoryOfJob CategoryOfJob { get; set; }
        public Partner? Partner { get; set; }
        public int PartnerId { get; set; }
        public string? OccupiedBy { get; set; }
        public DateTime? OccupiedFrom { get; set; }
        public DateTime? OccupiedTo { get; set; }

        public IList<JobOffer> JobOffers { get; set; } = new List<JobOffer>();
        public IList<AnnualClosure> AnnualClosures { get; set; } = new List<AnnualClosure>();
        public int? OccupiedJobOfferId { get; set; }
        public Job() { }

        public Job(
            string jobTitle,
            string jobNumber,
            string reward,
            CategoryOfJob categoryOfJob,
            Partner partner,
            string? comment = "",
            IList<AnnualClosure> annualClosures = null)
        {
            if (String.IsNullOrEmpty(jobTitle))
            {
                throw new ArgumentNullException(nameof(jobTitle));
            }

            JobTitle = jobTitle;
            JobNumber = jobNumber;
            Reward = reward;
            CategoryOfJob = categoryOfJob;
            Partner = partner;
            Comment = comment;
            if (annualClosures != null)
            {
                AnnualClosures = annualClosures.ToList();
            }
        }

        public JobOffer GetcurrentJobOffer()
        {
            return JobOffers.SingleOrDefault(offer =>
                offer.StatusOfJobOffer == StatusOfJobOffer.Open && offer.Softdelete != true);
        }

        public int getNumberOfCandidacies()
        {
            var result = 0;
            var jobOffer = JobOffers.SingleOrDefault(offer => offer.StatusOfJobOffer == StatusOfJobOffer.Open && offer.Softdelete != true);

            if (jobOffer != null)
            {
                result = jobOffer.Candidacies.Where(c => c.Softdelete != true).Count();
            }

            return result;
        }
    }
}