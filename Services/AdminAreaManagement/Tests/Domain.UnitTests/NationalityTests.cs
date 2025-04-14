using AdminAreaManagement.Core.Entities;
using FluentAssertions;
using Xunit;

namespace Domain.UnitTests;

public class NationalityTests
{
    [Fact]
    public void Constructor_WithValidName_ShouldCreateNationality()
    {
        // Arrange
        var name = "Belgian";

        // Act
        var nationality = new Nationality(name);

        // Assert
        nationality.Name.Should().Be(name);
    }

    [Fact]
    public void Constructor_WithNullName_ShouldThrowArgumentNullException()
    {
        // Arrange
        string name = null;

        // Act
        Action act = () => new Nationality(name);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("Name");
    }
}