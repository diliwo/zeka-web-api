using AdminAreaManagement.Application.Cities.Commands.CreateCity;
using AdminAreaManagement.Application.Professions.Commands.UpsertProfession;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using FluentValidation.TestHelper;
using Moq;
using Xunit;

namespace Application.UnitTests.Cities;

public class CreateCityCommandValidatorTests
{
    private readonly Mock<IRepositoryManager> _repositoryMock;
    private readonly Mock<ICityRepository> _cityRepositoryMock;
    private readonly CreateCityCommandValidator _validator;

    public CreateCityCommandValidatorTests()
    {
        _repositoryMock = new Mock<IRepositoryManager>();
        _cityRepositoryMock = new Mock<ICityRepository>();

        // Setup the repository to return an empty list by default
        _cityRepositoryMock.Setup(r => r.GetCities(It.IsAny<string>()))
            .Returns(new List<City>().AsQueryable());

        _repositoryMock.Setup(r => r.City).Returns(_cityRepositoryMock.Object);

        _validator = new CreateCityCommandValidator(_repositoryMock.Object);
    }

    [Fact]
    public async Task Should_Have_Error_When_Name_Is_Empty()
    {
        var command = new CreateCityCommand { Name = "", Country = "Belgium" };
        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Name)
              .WithErrorMessage("Name is required.");
    }

    [Fact]
    public async Task Should_Have_Error_When_Name_Exceeds_MaxLength()
    {
        var longName = new string('A', 101);
        var command = new CreateCityCommand { Name = longName, Country = "Belgium" };
        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Name)
              .WithErrorMessage("Number of chars must not exceed 50.");
    }

    [Fact]
    public async Task Should_Have_Error_When_Name_Is_Not_Unique()
    {
        var existingCities = new List<City>
        {
            new City { Name = "Brussels", Country = "Belgium" }
        };

        _cityRepositoryMock.Setup(r => r.GetCities(It.IsAny<string>()))
            .Returns(existingCities.AsQueryable());

        var command = new CreateCityCommand { Name = "Brussels", Country = "Belgium" };
        var result = await _validator.TestValidateAsync(command);
        result.ShouldHaveValidationErrorFor(c => c.Name)
              .WithErrorMessage("The specified city already exists.");
    }

    [Fact]
    public async Task Should_Not_Have_Error_When_All_Fields_Are_Valid_And_Unique()
    {
        var command = new CreateCityCommand { Name = "Antwerp", Country = "Belgium" };
        var result = await _validator.TestValidateAsync(command);
        result.ShouldNotHaveAnyValidationErrors();
    }
}