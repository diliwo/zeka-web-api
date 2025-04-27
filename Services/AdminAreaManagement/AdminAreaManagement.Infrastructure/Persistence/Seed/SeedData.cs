using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.ValueObjects;
using Microsoft.EntityFrameworkCore;

namespace AdminAreaManagement.Infrastructure.Persistence.Seed;

public static class SeedData
{
    public static void Seed(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<City>().HasData(
            new City { Id = 1,Name = "Brussels", Country = "Belgium", TenantName = "Zeka" },
            new City { Id = 2, Name = "Oslo", Country = "Norway", TenantName = "Zeka" },
            new City { Id = 3, Name = "Cape Town", Country = "South Africa", TenantName = "Zeka" }
        );

        modelBuilder.Entity<Nationality>().HasData(
            new Nationality { Id = 1, Name = "Belgian", TenantName = "Zeka" },
            new Nationality { Id = 2, Name = "Norway", TenantName = "Zeka" },
            new Nationality { Id = 3, Name = "South African", TenantName = "Zeka" }
        );

        modelBuilder.Entity<Profession>().HasData(
            new Profession { Id = 1, Name = "Kitchen, assistant" , TenantName = "Zeka" },
            new Profession { Id = 2, Name = "Baker" , TenantName = "Zeka" },
            new Profession { Id = 3, Name = "Computer repair technicien" , TenantName = "Zeka" }
        );

        modelBuilder.Entity<School>().HasData(
            new School { Id = 1, Name = "Massachusetts Institute Of Technology", Locality = "Cambridge" , TenantName = "Zeka" },
            new School { Id = 2, Name = "Stanford University", Locality = "Stanford" , TenantName = "Zeka" },
            new School { Id = 3, Name = "International University of Applied Science", Locality = "Berlin", TenantName = "Zeka" }
        );

        modelBuilder.Entity<StaffMember>().HasData(
            new StaffMember { Id = 1, FirstName = "John", LastName = "Doe", UserName = "Cambridge", TeamId = 1, TenantName = "Zeka" },
            new StaffMember { Id = 2, FirstName = "Helen", LastName = "Ripley", UserName = "hripley", TeamId = 3, TenantName = "Zeka" },
            new StaffMember { Id = 3, FirstName = "Adama", LastName = "Rezegova", UserName = "Cambridge", TeamId = 2, TenantName = "Zeka" }
        );

        modelBuilder.Entity<Team>().HasData(
            new Team { Id = 1, Name = "School & Education Service", Acronym = "SES", TenantName = "Zeka" },
            new Team { Id = 2, Name = "Imigration Service", Acronym = "IS" , TenantName = "Zeka"},
            new Team { Id = 3, Name = "Socio-Professional Integration Service", Acronym = "SPI" , TenantName = "Zeka" }
        );

        modelBuilder.Entity<TrainingField>().HasData(
            new TrainingField { Id = 1, Name = "Political Sociology" , TenantName = "Zeka" },
            new TrainingField { Id = 2, Name = "Information Technology" , TenantName = "Zeka" },
            new TrainingField { Id = 3, Name = "Languages" , TenantName = "Zeka" }
        );

        modelBuilder.Entity<TrainingType>().HasData(
            new TrainingType { Id = 1, Name = "Bachelor" , TenantName = "Zeka" },
            new TrainingType { Id = 2, Name = "Master", TenantName = "Zeka" },
            new TrainingType { Id = 3, Name = "Master of Business Administration", TenantName = "Zeka" }
        );

        modelBuilder.Entity<Training>().HasData(
            new Training { Id = 1, Name = "Software Developement", TrainingFieldId = 2, TenantName = "Zeka" },
            new Training { Id = 2, Name = "English", TrainingFieldId = 3, TenantName = "Zeka" },
            new Training { Id = 3, Name = "Deutch", TrainingFieldId = 3, TenantName = "Zeka" }
        );
    }
}