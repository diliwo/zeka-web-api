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
    public void Constructor_WithWhitespaceOnlyParameters_ShouldRejectEmptyNormalizedText(string name, string country)
    {
        Action act = () => new City(name, country);
        act.Should().Throw<ArgumentException>()
            .WithParameterName(string.IsNullOrWhiteSpace(name) ? "name" : "country");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Length_counts_normalized_Unicode_scalars_and_preserves_display_text(bool country)
    {
        var text = " \u00a0" + string.Concat(Enumerable.Repeat("e\u0301\U0001f600", 50)) + "\t";
        var city = new City(country ? "Brussels" : text, country ? text : "Belgium");
        (country ? city.Country : city.Name).Should().Be(text);
        var tooLong = text.Trim() + "a";
        Action construct = () => new City(country ? "Brussels" : tooLong, country ? tooLong : "Belgium");
        construct.Should().Throw<ArgumentException>().WithParameterName(country ? "country" : "name");
        Action mutate = () => { if (country) city.Country = tooLong; else city.Name = tooLong; };
        mutate.Should().Throw<ArgumentException>().WithParameterName(country ? "country" : "name");
        (country ? city.Country : city.Name).Should().Be(text);
    }

    [Fact]
    public void Malformed_Unicode_and_NUL_are_rejected()
    {
        foreach (var text in new[] { "\ud800", "A\0B" })
        {
            Action name = () => new City(text, "Belgium");
            Action country = () => new City("Brussels", text);
            name.Should().Throw<ArgumentException>().WithParameterName("name");
            country.Should().Throw<ArgumentException>().WithParameterName("country");
        }
    }
}
