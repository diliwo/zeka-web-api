using AdminAreaManagement.Core.Entities;
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
            if (!context.Teams.Any())
            {
                string currentDir = Directory.GetCurrentDirectory();

                //var path = Path.Combine("Persistence", "Seed", "Teams.json"); // uncomment When running in a docker container
                string path = "../AdminAreaManagement.Infrastructure/Persistence/Seed/Teams.json"; // Uncomment when running locally

                var teams = JsonConvert.DeserializeObject<List<Team>>(File.ReadAllText(path));

                if (teams != null)
                {
                    context.AddRange(teams);
                    context.SaveChanges();
                }
            }
        }
    }
}
