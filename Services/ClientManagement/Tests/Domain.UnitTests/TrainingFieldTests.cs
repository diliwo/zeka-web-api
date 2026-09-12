using ClientManagement.Core.Entities;
using Xunit;

namespace Domain.UnitTests;

public class TrainingFieldTests
{
    [Fact]
    public void Constructor_PreservesValidName()
    {
        var field = new TrainingField("Computer Science");

        Assert.Equal("Computer Science", field.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Constructor_RejectsMissingName(string? name)
    {
        var exception = Assert.Throws<ArgumentNullException>(() => new TrainingField(name!));

        Assert.Equal("name", exception.ParamName);
    }
}
