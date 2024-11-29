using AdminAreaManagement.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Newtonsoft.Json;

namespace AdminAreaManagement.Infrastructure.Persistence.DbContextExtensions
{
    public static class DbContextExtension
    {
        public static bool AllMigrationsApplied(this DbContext context)
        {
            var applied = context.GetService<IHistoryRepository>()
                .GetAppliedMigrations()
                .Select(m => m.MigrationId);

            var total = context.GetService<IMigrationsAssembly>()
                .Migrations
                .Select(m => m.Key);

            return !total.Except(applied).Any();
        }

        public static void EnsureSeeded(this ApplicationDbContext context)
        {
            //if (!context.MonitoringActions.Any())
            //{
            //    var monitoringActions = JsonConvert.DeserializeObject<List<MonitoringAction>>(File.ReadAllText(Path.Combine("Persistence", "Seed", "MonitoringActions.json")));

            //    if (monitoringActions != null)
            //    {
            //        context.AddRange(monitoringActions);
            //        context.SaveChanges();

            //    }
            //}

            //if (!context.EmploymentTerminationReasons.Any())
            //{
            //    var employmentTerminationReasons = JsonConvert.DeserializeObject<List<EmploymentTerminationReason>>(File.ReadAllText(Path.Combine("Persistence", "Seed", "EmploymentTerminationReasons.json")));

            //    if (employmentTerminationReasons != null)
            //    {
            //        context.AddRange(employmentTerminationReasons);
            //        context.SaveChanges();
            //    }

            //}

            //if (!context.EmploymentTerminationTypes.Any())
            //{
            //    var employmentTerminationTypes = JsonConvert.DeserializeObject<List<EmploymentTerminationType>>(File.ReadAllText(Path.Combine("Persistence", "Seed", "EmploymentTerminationTypes.json")));

            //    if (employmentTerminationTypes != null)
            //    {
            //        context.AddRange(employmentTerminationTypes);
            //        context.SaveChanges();
            //    }

            //}
        }
    }
}
