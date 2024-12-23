using ClientManagement.Core.Entities;

namespace ClientManagement.Core.Interfaces
{
    public interface ILanguageRepository
    {
        Task<List<Language>> SearchLanguages(string searchText = "");
    }
}
