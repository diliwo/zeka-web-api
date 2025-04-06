using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using LinqKit;
using Microsoft.EntityFrameworkCore;

namespace AdminAreaManagement.Infrastructure.Persistence;

public class CityRepository : ICityRepository
{
    private readonly ApplicationDbContext _context;

    public CityRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public void Persist(City places)
    {
        if (places.Id == default)
        {
            _context.Cities.Add(places);
        }
        else
        {
            _context.Cities.Update(places);
        }
        _context.SaveChanges();
    }

    public City GetById(int id)
    {
        return _context.Cities.FirstOrDefault(s => s.Id == id);
    }

    public IQueryable<City> GetPlaces(string filter = "")
    {
        var fields = _context.Cities.AsNoTracking().AsExpandable().Where(p => p.Softdelete != true);

        if (!string.IsNullOrEmpty(filter))
        {
            var predicate = PredicateBuilder.New<City>();

            predicate = predicate.Or(p => p.Country.ToLower().Contains(filter.ToLower().Trim()));

            predicate = predicate.Or(p => p.Name.ToLower().Contains(filter.ToLower().Trim()));

            predicate = predicate.Or(p => p.CountryDemonym.ToLower().Contains(filter.ToLower().Trim()));

            fields = fields.Where(predicate);
        }

        return fields;
    }

    public void SoftDelete(City city)
    {
        _context.Cities.Update(city);
        _context.SaveChanges();
    }
}