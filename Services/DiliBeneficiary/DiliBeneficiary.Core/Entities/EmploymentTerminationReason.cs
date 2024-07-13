using DiliBeneficiary.Core.Common;

namespace DiliBeneficiary.Core.Entities;

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