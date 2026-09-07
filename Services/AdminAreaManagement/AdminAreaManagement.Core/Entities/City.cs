using AdminAreaManagement.Core.Common;

namespace AdminAreaManagement.Core.Entities;

public class City : Entity
{
    public string Name { get; set; }
    public string Country { get; set; }

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
