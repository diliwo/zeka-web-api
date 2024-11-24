using Client.Core.Entities;

namespace Client.Core.Interfaces
{
    public interface ILanguageRepository
    {
        Task<List<Language>> SearchLanguages(string searchText = "");
    }
}
