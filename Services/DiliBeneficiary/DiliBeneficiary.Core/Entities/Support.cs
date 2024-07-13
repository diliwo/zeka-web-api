using DiliBeneficiary.Core.Common;

namespace DiliBeneficiary.Core.Entities
{
    public class Support : Entity
    {
        public DateTime StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public int ReferentId { get; set; }
        public Referent Referent { get; set; }
        public int BeneficiaryId { get; set; }
        public Beneficiary Beneficiary { get; set; }
        public String? Note { get; set; }
        public string? ReasonOfClosure { get; set; }

        public bool IsActif => !EndDate.HasValue;

        public Support()
        {
        }

        public Support(Beneficiary beneficiary,  DateTime startDate, Referent referent, String? note = "")
        {
            Beneficiary = beneficiary;
            StartDate = startDate;
            Referent = referent;
            Note = note;
        }
    }
}
