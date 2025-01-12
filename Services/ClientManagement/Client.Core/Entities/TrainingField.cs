using ClientManagement.Core.Common;

namespace ClientManagement.Core.Entities;

public class TrainingField : Entity
{
    public string Name { get; set; }

    TrainingField(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentNullException(nameof(name));
        }

        Name = name;
    }
}