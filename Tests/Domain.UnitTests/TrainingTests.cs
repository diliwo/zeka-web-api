using AdminAreaManagement.Core.Entities;

namespace Domain.UnitTests;

public class TrainingTests
{
    [Fact]
    public void Constructor_ShouldInitializeProperties_WhenValidArgumentsProvided()
    {
        // Arrange
        var name = "Software Development";
        var trainingField = new TrainingField { Name = "IT" };

        // Act
        var training = new Training(name, trainingField);

        // Assert
        Assert.Equal(name, training.Name);
        Assert.Equal(trainingField, training.TrainingField);
    }

    [Theory]
    [InlineData(null, "IT")]
    [InlineData("", "IT")]
    public void Constructor_ShouldThrowArgumentNullException_WhenNameIsNullOrEmpty(string name, string trainingFieldName)
    {
        // Arrange
        var trainingField = new TrainingField { Name = trainingFieldName };

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => new Training(name, trainingField));
        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void Constructor_ShouldThrowArgumentNullException_WhenTrainingFieldIsNull()
    {
        // Arrange
        var name = "Cyber Security";

        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => new Training(name, null));
        Assert.Equal("trainingField", exception.ParamName);
    }

    [Fact]
    public void TrainingFieldName_ShouldReturnCorrectFieldName()
    {
        // Arrange
        var trainingField = new TrainingField { Name = "Data Science" };
        var training = new Training("Machine Learning", trainingField);

        // Act
        var trainingFieldName = training.TrainingFieldName;

        // Assert
        Assert.Equal(trainingField.Name, trainingFieldName);
    }

    [Fact]
    public void Properties_ShouldAllowSettingAndGetting()
    {
        // Arrange
        var training = new Training();
        var name = "Artificial Intelligence";
        var trainingField = new TrainingField { Name = "Computer Science" };

        // Act
        training.Name = name;
        training.TrainingField = trainingField;

        // Assert
        Assert.Equal(name, training.Name);
        Assert.Equal(trainingField, training.TrainingField);
    }
}