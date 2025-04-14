using AdminAreaManagement.Core.Common;

namespace AdminAreaManagement.Core.Entities;

public class Nationality : Entity
{
    public string Name { get; set; }

    public Nationality(string name)
    {
        if(name == null) 
            throw new ArgumentNullException(nameof(Name));

        Name = name;
    }
}