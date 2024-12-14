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
                var path = Path.Combine("Persistence", "Seed", "Teams.json"); // uncomment When running in a docker container
                //string path = "../AdminAreaManagement.Infrastructure/Persistence/Seed/Teams.json"; // Uncomment when running locally

                var teams = JsonConvert.DeserializeObject<List<Team>>(File.ReadAllText(path));

                if (teams != null)
                {
                    context.AddRange(teams);
                    context.SaveChanges();
                }
            }

            if (!context.Professions.Any())
            { 
                var path = Path.Combine("Persistence", "Seed", "Professions.json"); // uncomment When running in a docker container
                //string path = "../AdminAreaManagement.Infrastructure/Persistence/Seed/Professions.json"; // Uncomment when running locally

                var professions = JsonConvert.DeserializeObject<List<Profession>>(File.ReadAllText(path));

                if (professions != null)
                {
                    context.AddRange(professions);
                    context.SaveChanges();
                }
            }

            if (!context.Schools.Any())
            {
                var path = Path.Combine("Persistence", "Seed", "Schools.json"); // uncomment When running in a docker container
                //string path = "../AdminAreaManagement.Infrastructure/Persistence/Seed/Schools.json"; // Uncomment when running locally

                var schools = JsonConvert.DeserializeObject<List<School>>(File.ReadAllText(path));

                if (schools != null)
                {
                    context.AddRange(schools);
                    context.SaveChanges();
                }
            }

            if (!context.TrainingFields.Any())
            {
                var path = Path.Combine("Persistence", "Seed", "TrainingFields.json"); // uncomment When running in a docker container
                //string path = "../AdminAreaManagement.Infrastructure/Persistence/Seed/TrainingFields.json"; // Uncomment when running locally

                var trainingFields = JsonConvert.DeserializeObject<List<TrainingField>>(File.ReadAllText(path));

                if (trainingFields != null)
                {
                    context.AddRange(trainingFields);
                    context.SaveChanges();
                }
            }

            if (!context.Trainings.Any())
            {
                var path = Path.Combine("Persistence", "Seed", "Trainings.json"); // uncomment When running in a docker container
                //string path = "../AdminAreaManagement.Infrastructure/Persistence/Seed/Trainings.json"; // Uncomment when running locally

                var Trainings = JsonConvert.DeserializeObject<List<Training>>(File.ReadAllText(path));

                if (Trainings != null)
                {
                    context.AddRange(Trainings);
                    context.SaveChanges();
                }
            }

            if (!context.TrainingTypes.Any())
            {
                var path = Path.Combine("Persistence", "Seed", "TrainingTypes.json"); // uncomment When running in a docker container
                //string path = "../AdminAreaManagement.Infrastructure/Persistence/Seed/TrainingTypes.json"; // Uncomment when running locally

                var trainingTypes = JsonConvert.DeserializeObject<List<TrainingType>>(File.ReadAllText(path));

                if (trainingTypes != null)
                {
                    context.AddRange(trainingTypes);
                    context.SaveChanges();
                }
            }

            if (!context.StaffMembers.Any())
            {
                var path = Path.Combine("Persistence", "Seed", "StaffMembers.json"); // uncomment When running in a docker container
                //string path = "../adminareamanagement.infrastructure/persistence/seed/staffmembers.json"; // uncomment when running locally

                var staffMembers = JsonConvert.DeserializeObject<List<StaffMember>>(File.ReadAllText(path));

                if (staffMembers != null)
                {
                    context.AddRange(staffMembers);
                    context.SaveChanges();
                }
            }

            if (!context.Partners.Any())
            {
                var path = Path.Combine("Persistence", "Seed", "Partners.json"); // uncomment When running in a docker container
                //string path = "../AdminAreaManagement.Infrastructure/Persistence/Seed/Partners.json"; // Uncomment when running locally

                var partners = JsonConvert.DeserializeObject<List<Partner>>(File.ReadAllText(path));

                if (partners != null)
                {
                    context.AddRange(partners);
                    context.SaveChanges();
                }
            }
        }
    }
}
