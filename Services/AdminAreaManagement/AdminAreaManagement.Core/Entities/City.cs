using AdminAreaManagement.Core.Common;

namespace AdminAreaManagement.Core.Entities;

public class City : Entity, Zeka.Extensions.MultiTenancy.Abstractions.IGlobalEntity
{
    private string _name = null!;
    private string _country = null!;

    public string Name
    {
        get => _name;
        set => _name = CityText.Validate(value, "name");
    }

    public string Country
    {
        get => _country;
        set => _country = CityText.Validate(value, "country");
    }

    public City() { }
    public City(string name, string country)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentNullException(nameof(name));
        }

        if (string.IsNullOrEmpty(country))
        {
            throw new ArgumentNullException(nameof(country));
        }

        Name = name;
        Country = country;
    }
}
