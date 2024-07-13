using DiliBeneficiary.Core.Entities;

namespace DiliBeneficiary.Core.Interfaces
{
    public interface ILanguageRepository
    {
        Task<List<Language>> SearchLanguages(string searchText = "");
    }
}
