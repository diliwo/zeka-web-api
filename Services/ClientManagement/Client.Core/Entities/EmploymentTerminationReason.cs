using ClientManagement.Core.Common;

namespace ClientManagement.Core.Entities;

public class EmploymentTerminationReason : Entity
{
    public string Name { get; set; }

    public EmploymentTerminationReason(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentNullException(nameof(name));
        }

        Name = name;
    }
}