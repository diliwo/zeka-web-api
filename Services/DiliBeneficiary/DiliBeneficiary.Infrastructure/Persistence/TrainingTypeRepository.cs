using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;
using LinqKit;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Infrastructure.Persistence;

public class TrainingTypeRepository : ITrainingTypeRepository
{
    private readonly ApplicationDbContext _context;

    public TrainingTypeRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public void Persist(TrainingType type)
    {
        if (type.Id == default(int))
        {
            _context.TrainingTypes.Add(type);
        }
        else
        {
            _context.TrainingTypes.Update(type);
        }
        _context.SaveChanges();
    }

    public TrainingType GetById(int typeId)
    {
        return _context.TrainingTypes.FirstOrDefault(s => s.Id == typeId);
    }

    public IQueryable<TrainingType> GetTypes(string filter = "", string orderBy = "")
    {
        var types = _context.TrainingTypes.AsNoTracking().AsExpandable().Where(p => p.Softdelete != true);

        if (!string.IsNullOrEmpty(filter))
        {
            var predicate = PredicateBuilder.New<TrainingType>();

            predicate = predicate.Or(p => p.Name.ToLower().Contains(filter.ToLower().Trim()));

            types = types.Where(predicate);
        }

        return types;
    }

    public void SoftDelete(TrainingType type)
    {
        _context.TrainingTypes.Update(type);
        _context.SaveChanges();
    }
}