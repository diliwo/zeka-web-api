using System;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using System.Text.RegularExpressions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using LinqKit;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace AdminAreaManagement.Infrastructure.Persistence
{
    public class TrainingsRepository : ITrainingsRepository
    {
        private readonly ApplicationDbContext _context;
        public TrainingsRepository(ApplicationDbContext context)
        {
            _context = context;
        }

        public void Persist(Training training)
        {
            if (training.Id == default(int))
            {
                _context.Trainings.Add(training);
            }
            else
            {
                _context.Trainings.Update(training);
            }
            _context.SaveChanges();
        }

        public Training GetById(int formationId)
        {
            return _context.Trainings.FirstOrDefault(s => s.Id == formationId);
        }

        public IQueryable<Training> GetTrainings(string filter = "")
        {
            var formations = _context.Trainings.AsNoTracking().Where(p => p.Softdelete != true);

            if (!string.IsNullOrEmpty(filter))
            {
                var predicate = PredicateBuilder.New<Training>();

                predicate = predicate.Or(p => p.Name.ToLower().Contains(filter.ToLower().Trim()));
                predicate = predicate.Or(p => p.TrainingField.Name.ToLower().Contains(filter.ToLower().Trim()));

                formations = formations.Where(predicate);
            }

            return formations;
        }

        public void SoftDelete(Training formation)
        {
            _context.Trainings.Update(formation);
            _context.SaveChanges();
        }
    }
}