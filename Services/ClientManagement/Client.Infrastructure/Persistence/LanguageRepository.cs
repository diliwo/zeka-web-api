using Client.Core.Entities;
using Client.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace Client.Infrastructure.Persistence
{
    public class LanguageRepository : ILanguageRepository
    {
        private readonly ApplicationDbContext _context;

        public LanguageRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public async Task<List<Language>> SearchLanguages(string searchText = "")
        {
            return await _context.Languages.Where(l => l.Name.ToLower().Contains(searchText.ToLower())).ToListAsync();
        }
    }
}
