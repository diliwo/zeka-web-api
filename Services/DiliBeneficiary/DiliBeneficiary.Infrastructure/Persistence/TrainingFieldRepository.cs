using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;
using LinqKit;
using Microsoft.EntityFrameworkCore;

namespace DiliBeneficiary.Infrastructure.Persistence;

public class TrainingFieldRepository : ITrainingFieldRepository
{
    private readonly ApplicationDbContext _context;

    public TrainingFieldRepository(ApplicationDbContext context)
    {
        _context = context;
    }

    public void Persist(TrainingField type)
    {
        if (type.Id == default(int))
        {
            _context.TrainingFields.Add(type);
        }
        else
        {
            _context.TrainingFields.Update(type);
        }
        _context.SaveChanges();
    }

    public TrainingField GetById(int typeId)
    {
        return _context.TrainingFields.FirstOrDefault(s => s.Id == typeId);
    }

    public IQueryable<TrainingField> GetFields(string filter = "", string orderBy = "")
    {
        var fields = _context.TrainingFields.AsNoTracking().AsExpandable().Where(p => p.Softdelete != true);

        if (!string.IsNullOrEmpty(filter))
        {
            var predicate = PredicateBuilder.New<TrainingField>();

            predicate = predicate.Or(p => p.Name.ToLower().Contains(filter.ToLower().Trim()));

            fields = fields.Where(predicate);
        }

        return fields;
    }

    public void SoftDelete(TrainingField type)
    {
        _context.TrainingFields.Update(type);
        _context.SaveChanges();
    }
}