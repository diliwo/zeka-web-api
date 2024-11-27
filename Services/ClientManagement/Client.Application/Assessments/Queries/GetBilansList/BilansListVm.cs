using Client.Application.Assessments.Common;

namespace Client.Application.Assessments.Queries.GetBilansList
{
    public class BilansListVm
    {
        public IList<AssessmentDto> Bilans { get; set; }
    }
}