using ClientManagement.Application.Assessments.Common;

namespace ClientManagement.Application.Assessments.Queries.GetBilansList
{
    public class BilansListVm
    {
        public IList<AssessmentDto> Bilans { get; set; }
    }
}