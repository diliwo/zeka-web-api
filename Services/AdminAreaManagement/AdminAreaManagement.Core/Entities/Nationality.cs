using AdminAreaManagement.Core.Common;

namespace AdminAreaManagement.Core.Entities;

public class Nationality : Entity, Zeka.Extensions.MultiTenancy.Abstractions.IGlobalEntity
{
    public string Name { get; set; }

    public Nationality(){}
    public Nationality(string name)
    {
        if(name == null)
            throw new ArgumentNullException(nameof(Name));

        Name = name;
    }
}
