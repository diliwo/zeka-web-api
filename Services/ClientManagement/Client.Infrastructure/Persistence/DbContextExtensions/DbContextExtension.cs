using Client.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Newtonsoft.Json;

namespace Client.Infrastructure.Persistence.DbContextExtensions
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

        //public static void EnsureSeeded(this ApplicationDbContext context)
        //{
        //    if (!context.MonitoringActions.Any())
        //    {
        //        var monitoringActions = JsonConvert.DeserializeObject<List<MonitoringAction>>(File.ReadAllText(@".." +
        //            Path.DirectorySeparatorChar + "Infrastructure" + Path.DirectorySeparatorChar + "Persistence" +
        //            Path.DirectorySeparatorChar + "Seed" + Path.DirectorySeparatorChar + "MonitoringActions.json"));
        //        context.AddRange(monitoringActions);
        //        context.SaveChanges();
        //    }
        //    if (!context.Languages.Any())
        //    {
        //        var languages = JsonConvert.DeserializeObject<List<Language>>(File.ReadAllText(@".." +
        //            Path.DirectorySeparatorChar + "Infrastructure" + Path.DirectorySeparatorChar + "Persistence" +
        //            Path.DirectorySeparatorChar + "Seed" + Path.DirectorySeparatorChar + "Languages.json"));

        //        if (languages != null)
        //        {
        //            context.AddRange(languages);
        //            context.SaveChanges();
        //        }
                
        //    }
        //}
    }
}
