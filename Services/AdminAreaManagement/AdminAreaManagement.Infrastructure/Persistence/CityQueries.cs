using AdminAreaManagement.Application.Cities;
using AdminAreaManagement.Application.Cities.Queries;
using AdminAreaManagement.Application.Common.Models;
using Microsoft.EntityFrameworkCore;
using System.Linq.Dynamic.Core;

namespace AdminAreaManagement.Infrastructure.Persistence;

public sealed class CityQueries(ApplicationDbContext context) : ICityQueries
{
    public Task<bool> ActiveCityExistsAsync(string name, string country, CancellationToken cancellationToken) =>
        context.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM "Cities"
                WHERE NOT "Softdelete"
                  AND "NormalizedName" = public.zeka_city_key({name}) COLLATE "C"
                  AND "NormalizedCountry" = public.zeka_city_key({country}) COLLATE "C"
            ) AS "Value"
            """).SingleAsync(cancellationToken);

    public async Task<PaginatedList<CityDto>> GetPageAsync(
        string filter, string orderBy, int pageNumber, int pageSize, CancellationToken cancellationToken)
    {
        var query = context.Cities.AsNoTracking().Where(city => !city.Softdelete);
        if (!string.IsNullOrEmpty(filter))
        {
            var search = filter.ToLower().Trim();
            query = query.Where(city => city.Name.ToLower().Contains(search) || city.Country.ToLower().Contains(search));
        }

        var projected = query.Select(city => new CityDto { Id = city.Id, Name = city.Name, Country = city.Country });
        var count = await projected.CountAsync(cancellationToken);
        // Sorting stays in Infrastructure too: no provider query is passed to an Application helper.
        var sorted = projected;
        if (count > 0 && !string.IsNullOrWhiteSpace(orderBy))
        {
            var properties = new[] { nameof(CityDto.Id), nameof(CityDto.Name), nameof(CityDto.Country) };
            var clauses = new List<string>();
            foreach (var part in orderBy.Trim().Split(','))
            {
                if (string.IsNullOrWhiteSpace(part)) continue;
                var requested = part.Split(' ')[0];
                var property = properties.FirstOrDefault(p => p.Equals(requested, StringComparison.InvariantCultureIgnoreCase));
                if (property is null) continue;
                clauses.Add(property + (part.EndsWith(" desc") ? " descending" : " ascending"));
            }
            sorted = projected.OrderBy(string.Join(", ", clauses));
        }
        var items = await sorted.Skip((pageNumber - 1) * pageSize).Take(pageSize).ToListAsync(cancellationToken);
        return new PaginatedList<CityDto>(items, pageNumber, string.Empty, pageSize, count);
    }
}
