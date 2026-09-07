using AdminAreaManagement.Application.Cities.Queries;
using AdminAreaManagement.Application.Common.Models;

namespace AdminAreaManagement.Application.Cities;

/// <summary>Queries shared reference data, independently of any organisation.</summary>
public interface ICityQueries
{
    /// <summary>Matches the active global normalized name/country key; ignores soft-deleted rows.</summary>
    Task<bool> ActiveCityExistsAsync(string name, string country, CancellationToken cancellationToken);

    Task<PaginatedList<CityDto>> GetPageAsync(
        string filter, string orderBy, int pageNumber, int pageSize, CancellationToken cancellationToken);
}
