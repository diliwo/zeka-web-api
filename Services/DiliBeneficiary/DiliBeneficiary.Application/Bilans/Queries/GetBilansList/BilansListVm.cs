using DiliBeneficiary.Application.Bilans.Common;

namespace DiliBeneficiary.Application.Bilans.Queries.GetBilansList
{
    public class BilansListVm
    {
        public IList<BilanDto> Bilans { get; set; }
    }
}