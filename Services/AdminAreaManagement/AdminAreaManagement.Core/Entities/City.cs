using AdminAreaManagement.Core.Common;

namespace AdminAreaManagement.Core.Entities;

public class City : Entity
{
    public string Name { get; set; }
    public string Country { get; set; }
    public string CountryDemonym { get; set; }

    public City() { }
    public City(string name, string country, string countryDemonym)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentNullException(nameof(name));
        }

        if (string.IsNullOrEmpty(Country))
        {
            throw new ArgumentNullException(nameof(country));
        }

        if ( string.IsNullOrEmpty(CountryDemonym))
        {
            throw new InvalidOperationException(nameof(countryDemonym));
        }

        Name = name;
        Country = country;
        CountryDemonym = countryDemonym;
    }
}