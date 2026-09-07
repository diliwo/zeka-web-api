using AdminAreaManagement.Application.Cities;
using AdminAreaManagement.Application.Cities.Commands.DeleteClity;
using AdminAreaManagement.Application.Cities.Queries;
using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using FluentAssertions;
using Moq;
using Xunit;

namespace Application.UnitTests.Cities;

public class CityQueryAndRestorationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Restoration_checks_the_active_global_key_before_persisting(bool duplicate)
    {
        using var source = new CancellationTokenSource();
        var city = new City("Brussels", "Belgium") { Id = 7, Softdelete = true };
        var repository = new Mock<IRepositoryManager>(MockBehavior.Strict);
        var cities = new Mock<ICityQueries>(MockBehavior.Strict);
        repository.Setup(r => r.City.GetById(7)).Returns(city);
        cities.Setup(c => c.ActiveCityExistsAsync(city.Name, city.Country, source.Token)).ReturnsAsync(duplicate);
        if (!duplicate) repository.Setup(r => r.City.SoftDelete(city));
        var handler = new DeleteCityCommand.DeleteCityCommandHandler(repository.Object, cities.Object);

        Func<Task> act = () => handler.Handle(new DeleteCityCommand { Id = 7 }, source.Token);
        if (duplicate)
        {
            var exception = await act.Should().ThrowAsync<ValidationException>();
            exception.Which.Errors["Name"].Should().Equal("The specified city already exists.");
            city.Softdelete.Should().BeTrue();
            repository.Verify(r => r.City.SoftDelete(It.IsAny<City>()), Times.Never);
        }
        else
        {
            await act();
            city.Softdelete.Should().BeFalse();
            repository.Verify(r => r.City.SoftDelete(city), Times.Once);
        }
        cities.Verify(c => c.ActiveCityExistsAsync(city.Name, city.Country, source.Token), Times.Once);
    }

    [Fact]
    public async Task Deletion_does_not_query_uniqueness()
    {
        var city = new City("Brussels", "Belgium") { Id = 7 };
        var repository = new Mock<IRepositoryManager>(MockBehavior.Strict);
        var cities = new Mock<ICityQueries>(MockBehavior.Strict);
        repository.Setup(r => r.City.GetById(7)).Returns(city);
        repository.Setup(r => r.City.SoftDelete(city));
        await new DeleteCityCommand.DeleteCityCommandHandler(repository.Object, cities.Object)
            .Handle(new DeleteCityCommand { Id = 7 }, CancellationToken.None);
        city.Softdelete.Should().BeTrue();
        cities.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Listing_passes_paging_and_cancellation_through_a_materialized_port()
    {
        using var source = new CancellationTokenSource();
        var cities = new Mock<ICityQueries>(MockBehavior.Strict);
        var page = new PaginatedList<CityDto>(new(), 2, "", 10, 11);
        cities.Setup(c => c.GetPageAsync("bel", "Name desc", 2, 10, source.Token)).ReturnsAsync(page);
        var result = await new GetCitiesListQuery.GetCitiesListQueryHandler(cities.Object).Handle(
            new GetCitiesListQuery { Filter = "bel", OrderBy = "Name desc", PageNumber = 2, PageSize = 10 }, source.Token);
        result.Should().BeSameAs(page);
        cities.VerifyAll();
    }

    [Fact]
    public void City_ports_expose_no_queryable_or_provider_types()
    {
        foreach (var method in typeof(ICityQueries).GetMethods().Concat(typeof(ICityRepository).GetMethods()))
        {
            var signatures = method.GetParameters().Select(p => p.ParameterType).Append(method.ReturnType);
            signatures.Should().NotContain(type => type.ToString().Contains("IQueryable") ||
                type.ToString().Contains("EntityFrameworkCore"));
        }
        Assert.Same(typeof(GetCitiesListQuery).Assembly, typeof(ICityQueries).Assembly);
        typeof(City).GetProperty("OrganisationId").Should().BeNull();
    }
}
