using AdminAreaManagement.Core.Entities;

namespace Domain.UnitTests;

public class TrainingFieldTests
{
    [Fact]
    public void Constructor_ShouldInitializeName_WhenValidNameProvided()
    {
        // Arrange
        var name = "Computer Science";

        // Act
        var trainingField = new TrainingField(name);

        // Assert
        Assert.Equal(name, trainingField.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Constructor_ShouldThrowArgumentNullException_WhenNameIsNullOrEmpty(string invalidName)
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => new TrainingField(invalidName));
        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void DefaultConstructor_ShouldInitializeWithNullName()
    {
        // Act
        var trainingField = new TrainingField();

        // Assert
        Assert.Null(trainingField.Name);
    }

    [Fact]
    public void NameProperty_ShouldAllowSettingAndGetting()
    {
        // Arrange
        var trainingField = new TrainingField();
        var name = "Artificial Intelligence";

        // Act
        trainingField.Name = name;

        // Assert
        Assert.Equal(name, trainingField.Name);
    }
}