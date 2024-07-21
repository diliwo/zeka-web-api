using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Infrastructure.Persistence
{
    public class BilanRepository : IBilanRepository
    {
        private readonly ApplicationDbContext _context;

        public BilanRepository(ApplicationDbContext context)
        {
            _context = context;
        }


        public int Persist(Bilan bilan)
        {
            if (bilan.Id == default(int))
            {
                _context.Bilans.Add(bilan);
            }
            else
            {
                _context.Bilans.Update(bilan);
            }
            _context.SaveChanges();

            return bilan.Id;
        }

        public IQueryable<Bilan> GetBilans()
        {
            return from bilans in _context.Bilans where bilans.Softdelete != true select bilans;
        }

        public IQueryable<Bilan> GetBilans(int beneficiaryId)
        {
            return from bilans in _context.Bilans 
                where (bilans.BeneficiaryId == beneficiaryId && bilans.Softdelete != true) select bilans;
        }

        public Bilan GetBilanById(int id)
        {
            return _context.Bilans.Include(b => b.BilanProfessions).ThenInclude(bp => bp.Profession).FirstOrDefault(s => s.Id == id);
        }

        public Bilan GetCurrentBilan()
        {
            return _context.Bilans.FirstOrDefault(b => b.IsFinalized != true);
        }

        public IQueryable<Bilan> GetArchivedBilans()
        {
            return from bilans in _context.Bilans where bilans.IsFinalized != false select bilans;
        }

        public void SoftDelete(Bilan bilan)
        {
            if (bilan.Softdelete)
            {
                bilan.Softdelete = false;
            }
            else
            {
                bilan.Softdelete = true;
            }

            foreach (var bilanProfession in bilan.BilanProfessions)
            {
                bilanProfession.BilanId = bilanProfession.BilanId - 1;
            }

            _context.Bilans.Update(bilan);
            _context.SaveChanges();     
        }

        public async Task<bool> IsBilanNotFinalizd(int bilanId)
        {
            var bilan = _context.Bilans.SingleOrDefaultAsync(b => b.Id == bilanId);

            if (bilan.Result == null)
            {
                return false;
            }

            return !bilan.Result.IsFinalized ? true : false;
        }

        public async Task<bool> AreAllBilansNotFinalized(int beneficiaryId)
        {
            var bilans = _context.Bilans.Where(b => b.BeneficiaryId == beneficiaryId && b.Softdelete != true);

            if (bilans == null)
            {
                return false;
            }

            return !bilans.Any(s => s.IsFinalized == false);
        }
    }
}