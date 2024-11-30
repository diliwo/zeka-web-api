using ClientManagement.Core.Common;

namespace ClientManagement.Core.Entities;

public class EmploymentTerminationType : Entity
{
    public string Name { get; set; }

    public EmploymentTerminationType(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentNullException(nameof(name));
        }

        Name = name;
    }
}