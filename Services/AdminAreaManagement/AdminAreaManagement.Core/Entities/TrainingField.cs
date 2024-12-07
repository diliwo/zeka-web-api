using AdminAreaManagement.Core.Common;

namespace AdminAreaManagement.Core.Entities;

public class TrainingField : Entity
{
    public string Name { get; set; }

    public TrainingField(){}
    public TrainingField(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            throw new ArgumentNullException(nameof(name));
        }

        Name = name;
    }
}