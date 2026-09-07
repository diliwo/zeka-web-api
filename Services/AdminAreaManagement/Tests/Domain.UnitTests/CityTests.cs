using AdminAreaManagement.Core.Entities;
using FluentAssertions;
using Xunit;

namespace Domain.UnitTests;

public class CityTests
{
    [Fact]
    public void Constructor_WithValidParameters_ShouldCreateCity()
    {
        // Arrange
        var name = "Brussels";
        var country = "Belgium";

        // Act
        var city = new City(name, country);

        // Assert
        city.Name.Should().Be(name);
        city.Country.Should().Be(country);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Constructor_WithNullOrEmptyName_ShouldThrowArgumentNullException(string invalidName)
    {
        // Arrange
        var country = "Belgium";

        // Act
        Action act = () => new City(invalidName, country);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("name");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Constructor_WithNullOrEmptyCountry_ShouldThrowArgumentNullException(string invalidCountry)
    {
        // Arrange
        var name = "Brussels";

        // Act
        Action act = () => new City(name, invalidCountry);

        // Assert
        act.Should().Throw<ArgumentNullException>()
            .WithParameterName("country");
    }

    [Theory]
    [InlineData(" ", "Belgium")]
    [InlineData("\t\r\n", "Belgium")]
    [InlineData("Brussels", " ")]
    [InlineData("Brussels", "\t\r\n")]
    public void Constructor_WithWhitespaceOnlyParameters_ShouldPreserveValues(string name, string country)
    {
        var city = new City(name, country);

        city.Name.Should().Be(name);
        city.Country.Should().Be(country);
    }
}
