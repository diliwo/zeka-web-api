using AdminAreaManagement.Core.Entities;

namespace Domain.UnitTests;

public class SchoolTests
{
    [Fact]
    public void Constructor_ShouldInitializeNameAndLocality_WhenValidValuesProvided()
    {
        // Arrange
        var name = "Harvard";
        var locality = "Cambridge";

        // Act
        var school = new School(name, locality);

        // Assert
        Assert.Equal(name, school.Name);
        Assert.Equal(locality, school.Locality);
    }

    [Theory]
    [InlineData(null, "Locality")]
    [InlineData("", "Locality")]
    [InlineData("School Name", null)]
    [InlineData("School Name", "")]
    public void Constructor_ShouldThrowArgumentNullException_WhenNameOrLocalityIsNullOrEmpty(string name, string locality)
    {
        // Act & Assert
        if (string.IsNullOrEmpty(name))
        {
            var exception = Assert.Throws<ArgumentNullException>(() => new School(name, locality));
            Assert.Equal("name", exception.ParamName);
        }
        else if (string.IsNullOrEmpty(locality))
        {
            var exception = Assert.Throws<ArgumentNullException>(() => new School(name, locality));
            Assert.Equal("locality", exception.ParamName);
        }
    }

    [Fact]
    public void NameAndLocalityProperties_ShouldAllowSettingAndGetting()
    {
        // Arrange
        var school = new School("Initial Name", "Initial Locality");
        var newName = "MIT";
        var newLocality = "Boston";

        // Act
        school.Name = newName;
        school.Locality = newLocality;

        // Assert
        Assert.Equal(newName, school.Name);
        Assert.Equal(newLocality, school.Locality);
    }
}