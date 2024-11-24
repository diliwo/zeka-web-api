using Client.Application.Bilans.Common;

namespace Client.Application.Bilans.Queries.GetBilansList
{
    public class BilansListVm
    {
        public IList<BilanDto> Bilans { get; set; }
    }
}