using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Infrastructure.Persistence
{
    public class ProfessionBilanRepository : IProfessionBilanRepository
    {
        private readonly ApplicationDbContext _context;

        public ProfessionBilanRepository() { }

        public ProfessionBilanRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public void Persist(BilanProfession bilanProfession)
        {
            if (bilanProfession.Id == default(int))
            {
                _context.ProfessionBilans.Add(bilanProfession);
            }
            else
            {
                _context.ProfessionBilans.Update(bilanProfession);
            }
            _context.SaveChanges();
        }

        public BilanProfession Get(int professionBilanId)
        {
            return _context.ProfessionBilans.FirstOrDefault(c => c.Id == professionBilanId && c.Softdelete != true);
        }

        public Task<BilanProfession> GetASync(int professionBilanId)
        {
            return _context.ProfessionBilans.FirstOrDefaultAsync(c => c.Id == professionBilanId && c.Softdelete != true);
        }

        public IQueryable<BilanProfession> GetProfessionBilans()
        {
            return _context.ProfessionBilans.Where(c => c.Softdelete != true);
        }

        public IQueryable GetProfessionByBilanId(int id)
        {
            return _context.ProfessionBilans.Where(s => s.BilanId == id && s.Softdelete != true);
        }

        public void SoftDelete(BilanProfession bilanProfession)
        {
            _context.ProfessionBilans.Update(bilanProfession);
            _context.SaveChanges();
        }
    }
}