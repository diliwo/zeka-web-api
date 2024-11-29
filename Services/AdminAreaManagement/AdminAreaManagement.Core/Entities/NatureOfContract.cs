using AdminAreaManagement.Core.Commun;

namespace AdminAreaManagement.Core.Entities;

public class NatureOfContract : Entity
{
    public string Name { get; set; }

    public NatureOfContract(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentNullException(nameof(name));
        }

        Name = name;
    }
}