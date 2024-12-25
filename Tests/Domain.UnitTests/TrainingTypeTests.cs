using AdminAreaManagement.Core.Entities;

namespace Domain.UnitTests;

public class TrainingTypeTests
{
    [Fact]
    public void Constructor_ShouldInitializeName_WhenValidNameProvided()
    {
        // Arrange
        var name = "Technical Training";

        // Act
        var trainingType = new TrainingType(name);

        // Assert
        Assert.Equal(name, trainingType.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Constructor_ShouldThrowArgumentNullException_WhenNameIsNullOrEmpty(string invalidName)
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => new TrainingType(invalidName));
        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void NameProperty_ShouldAllowSettingAndGetting()
    {
        // Arrange
        var trainingType = new TrainingType("Initial Name");
        var newName = "Leadership Training";

        // Act
        trainingType.Name = newName;

        // Assert
        Assert.Equal(newName, trainingType.Name);
    }
}