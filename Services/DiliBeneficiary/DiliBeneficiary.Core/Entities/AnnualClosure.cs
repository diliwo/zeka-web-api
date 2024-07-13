using DiliBeneficiary.Core.Common;

namespace DiliBeneficiary.Core.Entities
{
    public class AnnualClosure : Entity
    {
        public int StartDay { get; set; }
        public int StartMonth { get; set; }
        public int EndDay { get; set; }
        public int EndMonth { get; set; }
        public int JobId { get; set; }
        public Job Job { get; set; }
    }
}
