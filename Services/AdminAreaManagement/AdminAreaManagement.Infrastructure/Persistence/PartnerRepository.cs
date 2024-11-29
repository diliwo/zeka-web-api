using LinqKit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Dynamic.Core;
using System.Text;
using System.Threading.Tasks;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Enums;
using AdminAreaManagement.Core.Interfaces;
using AdminAreaManagement.Infrastructure.Persistence;

namespace Cpas.Zeka.Infrastructure.Persistence
{
    public class PartnerRepository : IPartnerRepository
    {
        private readonly ApplicationDbContext _context;

        public PartnerRepository() { }

        public PartnerRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public void Persist(Partner partner)
        {
            if (partner.Id == default(int))
            {
                _context.Partners.Add(partner);
            }
            else
            {
                _context.Partners.Update(partner);
            }
            _context.SaveChanges();
        }

        public Partner Get(int id)
        {
            return _context.Partners.FirstOrDefault(b => b.Id == id && b.Softdelete != true);
        }

        public IQueryable<Partner> GetPartners(string filter = null, bool onlyActive = false)
        {

            var partners = _context.Partners.AsExpandable().Where(p => p.Softdelete != true);
            var resultToInt = 0;


            if (!string.IsNullOrEmpty(filter))
            {
                var predicate = PredicateBuilder.New<Partner>();

                predicate = predicate.Or(p => p.Name.ToLower().Contains(filter.ToLower().Trim()));
                predicate = predicate.Or(p => p.StaffMember.FirstName.ToLower().Contains(filter.ToLower().Trim()));
                predicate = predicate.Or(p => p.StaffMember.LastName.ToLower().Contains(filter.ToLower().Trim()));

                if (int.TryParse(filter, out resultToInt))
                    predicate = predicate.Or(p => p.PartnerNumber == resultToInt);

                partners = partners.Where(predicate);
            }

            if (onlyActive)
            {
                partners = partners.Where(p => p.StatusOfPartner == StatusOfPartner.Active);
            }
            return partners;
        }

        public void SoftDelete(Partner referent)
        {
            _context.Partners.Update(referent);
            _context.SaveChanges();
        }

        public void Dispose()
        {
            if (_context != null)
            {
                _context.Dispose();
            }
        }
    }
}
