using DiliBeneficiary.Core.Common;

namespace DiliBeneficiary.Core.Entities
{
    public class QuarterlyMonitoring : Entity
    {
        public QuarterlyMonitoring()
        {
        }

        public int BeneficiaryId { get; set; }
        public Beneficiary Beneficiary { get; set; }
        public int ReferentId { get; set; }
        public Referent Referent { get; set; }
        public int MonitoringActionId { get; set; }
        public MonitoringAction MonitoringAction { get; set; }
        public DateTime ActionDate { get; set; } = DateTime.Now;
        public string ActionComment { get; set; } = string.Empty;
        private string _quarter;
        public string Quarter
        {
            get => ActionDate.Year.ToString() + "Q" + ((ActionDate.Month - 1) / 3 + 1).ToString();
            set => _quarter = value;
        }

        public QuarterlyMonitoring(Beneficiary beneficiary, Referent referent, MonitoringAction monitoringAction,
            DateTime actionDate, string actionComment)
        {
            Beneficiary = beneficiary;
            Referent = referent;
            MonitoringAction = monitoringAction;
            ActionDate = actionDate;
            ActionComment = actionComment;
        }

        public QuarterlyMonitoring(int beneficiaryId, int referentId, int monitoringActionId, DateTime actionDate,
            string actionComment)
        {
            BeneficiaryId = beneficiaryId;
            ReferentId = referentId;
            MonitoringActionId = monitoringActionId;
            ActionDate = actionDate;
            ActionComment = actionComment;

        }
    }
}
