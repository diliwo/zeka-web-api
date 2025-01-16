using AdminAreaManagement.Core.Entities;
using Xunit;

namespace Domain.UnitTests;

public class ProfessionTests
{
    [Fact]
    public void Constructor_ShouldInitializeName_WhenValidNameProvided()
    {
        // Arrange
        var name = "Software Engineer";

        // Act
        var profession = new Profession(name);

        // Assert
        Assert.Equal(name, profession.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Constructor_ShouldThrowArgumentNullException_WhenNameIsNullOrEmpty(string invalidName)
    {
        // Act & Assert
        var exception = Assert.Throws<ArgumentNullException>(() => new Profession(invalidName));
        Assert.Equal("name", exception.ParamName);
    }

    [Fact]
    public void DefaultConstructor_ShouldInitializeWithNullName()
    {
        // Act
        var profession = new Profession();

        // Assert
        Assert.Null(profession.Name);
    }

    [Fact]
    public void NameProperty_ShouldAllowSettingAndGetting()
    {
        // Arrange
        var profession = new Profession();
        var name = "Doctor";

        // Act
        profession.Name = name;

        // Assert
        Assert.Equal(name, profession.Name);
    }
}