using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;

namespace AdminAreaManagement.Infrastructure.Persistence;

public class CityRepository : ICityRepository
{
    private readonly ApplicationDbContext _context;

    public CityRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public void Persist(City city)
    {
        if (city.Id == default)
        {
            _context.Cities.Add(city);
        }
        else
        {
            _context.Cities.Update(city);
        }
        _context.SaveChanges();
    }

    public City GetById(int id)
    {
        return _context.Cities.FirstOrDefault(s => s.Id == id);
    }

    public void SoftDelete(City city)
    {
        _context.Cities.Update(city);
        _context.SaveChanges();
    }
}
