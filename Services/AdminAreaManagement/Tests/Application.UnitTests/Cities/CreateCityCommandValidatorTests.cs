using AdminAreaManagement.Application.Cities;
using AdminAreaManagement.Application.Cities.Commands.CreateCity;
using FluentAssertions;
using FluentValidation.TestHelper;
using Moq;
using Xunit;

namespace Application.UnitTests.Cities;

public class CreateCityCommandValidatorTests
{
    private readonly Mock<ICityQueries> _cities = new(MockBehavior.Strict);
    private readonly CreateCityCommandValidator _validator;

    public CreateCityCommandValidatorTests() => _validator = new(_cities.Object);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("\u00a0\u2003")]
    public async Task Should_Have_Error_When_Name_Is_Empty(string? name)
    {
        var result = await _validator.TestValidateAsync(new CreateCityCommand { Name = name!, Country = "Belgium" });
        result.ShouldHaveValidationErrorFor(c => c.Name).WithErrorMessage("Name is required.");
        _cities.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Should_Have_Error_When_Name_Exceeds_MaxLength()
    {
        var result = await _validator.TestValidateAsync(new CreateCityCommand { Name = new string('A', 101), Country = "Belgium" });
        result.ShouldHaveValidationErrorFor(c => c.Name)
            .WithErrorMessage("Name must not exceed 100 Unicode scalar values after normalization and trimming.");
        _cities.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Should_Have_Error_When_Name_Is_Not_Unique()
    {
        _cities.Setup(c => c.ActiveCityExistsAsync("Brussels", "Belgium", It.IsAny<CancellationToken>())).ReturnsAsync(true);
        var result = await _validator.TestValidateAsync(new CreateCityCommand { Name = "Brussels", Country = "Belgium" });
        result.ShouldHaveValidationErrorFor(c => c.Name).WithErrorMessage("The specified city already exists.");
        result.ShouldNotHaveValidationErrorFor(c => c.Country);
    }

    [Fact]
    public async Task Should_Not_Have_Error_When_All_Fields_Are_Valid_And_Unique()
    {
        _cities.Setup(c => c.ActiveCityExistsAsync("Antwerp", "Belgium", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        var result = await _validator.TestValidateAsync(new CreateCityCommand { Name = "Antwerp", Country = "Belgium" });
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t\r\n")]
    [InlineData("\u00a0\u2003")]
    public async Task Country_is_required_without_calling_persistence(string? country)
    {
        var result = await _validator.TestValidateAsync(new CreateCityCommand { Name = "Brussels", Country = country! });
        result.ShouldHaveValidationErrorFor(c => c.Country).WithErrorMessage("Country is required.");
        _cities.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scalar_length_is_checked_after_NFC_and_trim(bool country)
    {
        var text = " \u00a0" + string.Concat(Enumerable.Repeat("e\u0301\U0001f600", 50)) + "\t";
        var command = new CreateCityCommand { Name = country ? "Brussels" : text, Country = country ? text : "Belgium" };
        _cities.Setup(c => c.ActiveCityExistsAsync(command.Name, command.Country, It.IsAny<CancellationToken>())).ReturnsAsync(false);
        (await _validator.TestValidateAsync(command)).ShouldNotHaveAnyValidationErrors();
        _cities.Invocations.Clear();

        if (country) command.Country = text.Trim() + "a";
        else command.Name = text.Trim() + "a";
        var result = await _validator.TestValidateAsync(command);
        result.Errors.Should().ContainSingle().Which.PropertyName.Should().Be(country ? "Country" : "Name");
        result.Errors.Single().ErrorMessage.Should().Be(
            $"{(country ? "Country" : "Name")} must not exceed 100 Unicode scalar values after normalization and trimming.");
        _cities.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Both_display_fields_and_cancellation_reach_the_global_semantic_port_once()
    {
        using var source = new CancellationTokenSource();
        var command = new CreateCityCommand { Name = "  Lie\u0300ge ", Country = " BELGIUM " };
        _cities.Setup(c => c.ActiveCityExistsAsync(command.Name, command.Country, source.Token)).ReturnsAsync(false);
        (await _validator.TestValidateAsync(command, cancellationToken: source.Token)).ShouldNotHaveAnyValidationErrors();
        _cities.Verify(c => c.ActiveCityExistsAsync(command.Name, command.Country, source.Token), Times.Once);
        _cities.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Cancellation_from_the_port_is_propagated()
    {
        using var source = new CancellationTokenSource();
        _cities.Setup(c => c.ActiveCityExistsAsync("Brussels", "Belgium", source.Token))
            .ThrowsAsync(new OperationCanceledException(source.Token));
        Func<Task> act = () => _validator.ValidateAsync(
            new CreateCityCommand { Name = "Brussels", Country = "Belgium" }, source.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Invalid_Unicode_is_rejected_before_persistence()
    {
        foreach (var value in new[] { "\ud800", "A\0B" })
        {
            var result = await _validator.TestValidateAsync(new CreateCityCommand { Name = value, Country = value });
            result.ShouldHaveValidationErrorFor(c => c.Name).WithErrorMessage("Name must be valid Unicode text.");
            result.ShouldHaveValidationErrorFor(c => c.Country).WithErrorMessage("Country must be valid Unicode text.");
        }
        _cities.VerifyNoOtherCalls();
    }
}
