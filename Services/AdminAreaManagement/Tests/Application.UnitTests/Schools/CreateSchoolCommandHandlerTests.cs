using AdminAreaManagement.Application.Professions.Commands.DeleteProfession;
using AdminAreaManagement.Application.Professions.Commands.UpsertProfession;
using AdminAreaManagement.Application.Schools.Commands.CreateSchool;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using FluentAssertions;
using Moq;
using System.Reflection;
using Xunit;

namespace Application.UnitTests.Schools;

public class CreateSchoolCommandHandlerTests
{
    private readonly Mock<IRepositoryManager> _repositoryMock;
    private readonly Mock<ISchoolRepository> _schoolRepositoryMock;
    private readonly CreateSchoolCommand.CreateSchoolCommandHandler _handler;

    public CreateSchoolCommandHandlerTests()
    {
        _repositoryMock = new Mock<IRepositoryManager>();
        _schoolRepositoryMock = new Mock<ISchoolRepository>();
        _repositoryMock.Setup(r => r.School).Returns(_schoolRepositoryMock.Object);
        _handler = new CreateSchoolCommand.CreateSchoolCommandHandler(_repositoryMock.Object);
    }

    [Fact]
    public async Task Handle_ShouldCreateSchool_WhenNameAndLocalityAreValid()
    {
        // Arrange
        var command = new CreateSchoolCommand { Name = "Greenwood High", Locality = "Springfield" };
        var school = new School(command.Name, command.Locality);
        _schoolRepositoryMock.Setup(r => r.Persist(It.IsAny<School>())).Callback<School>(s =>
        {
            var schoolType = typeof(School);
            var idProperty = schoolType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (idProperty != null && idProperty.CanWrite)
            {
                idProperty.SetValue(s, 1); // Set the Id to 1
            }
        });

        _repositoryMock.Setup(r => r.Save());

        // Act
        var result = await _handler.Handle(command, CancellationToken.None);

        // Assert
        result.Should().Be(1);
        _schoolRepositoryMock.Verify(r => r.Persist(It.Is<School>(s => s.Name == "Greenwood High" && s.Locality == "Springfield")), Times.Once);
        _repositoryMock.Verify(r => r.Save(), Times.Once);
    }

    [Theory]
    [InlineData(null, "Springfield")]
    [InlineData("", "Springfield")]
    public async Task Handle_ShouldThrowArgumentException_WhenNameIsNullOrEmpty(string name, string locality)
    {
        // Arrange
        var command = new CreateSchoolCommand { Name = name, Locality = locality };

        // Act
        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        // Assert
        var exception = await act.Should().ThrowExactlyAsync<ArgumentNullException>();
        exception.WithParameterName("name")
            .WithMessage(new ArgumentNullException("name", "name cannot be null or empty.").Message);
        _schoolRepositoryMock.Verify(r => r.Persist(It.IsAny<School>()), Times.Never);
        _repositoryMock.Verify(r => r.Save(), Times.Never);
    }

    [Theory]
    [InlineData("Greenwood High", null)]
    [InlineData("Greenwood High", "")]
    public async Task Handle_ShouldThrowArgumentException_WhenLocalityIsNullOrEmpty(string name, string locality)
    {
        // Arrange
        var command = new CreateSchoolCommand { Name = name, Locality = locality };

        // Act
        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        // Assert
        var exception = await act.Should().ThrowExactlyAsync<ArgumentNullException>();
        exception.WithParameterName("locality")
            .WithMessage(new ArgumentNullException("locality", "locality cannot be null or empty.").Message);
        _schoolRepositoryMock.Verify(r => r.Persist(It.IsAny<School>()), Times.Never);
        _repositoryMock.Verify(r => r.Save(), Times.Never);
    }
}
