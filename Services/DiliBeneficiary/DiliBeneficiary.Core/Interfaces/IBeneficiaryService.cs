namespace DiliBeneficiary.Core.Interfaces;

public interface IBeneficiaryService
{
    Task<int> Update(List<string> nisses);
    Task<int> UpSert(string niss);
}