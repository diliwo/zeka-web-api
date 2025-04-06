using System.Reflection;
using AdminAreaManagement.Application.Professions.Commands.UpsertProfession;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using FluentAssertions;
using Moq;
using Xunit;

namespace Application.UnitTests.Professions
{
    public class UpsertProfessionCommandTest
    {
        private readonly Mock<IRepositoryManager> _repositoryMock;
        private readonly Mock<IProfessionRepository> _professionRepositoryMock;
        private readonly UpsertProfessionCommand.UpsertProfessionCommandHandler _handler;

        public UpsertProfessionCommandTest()
        {
            _repositoryMock = new Mock<IRepositoryManager>();
            _professionRepositoryMock = new Mock<IProfessionRepository>();
            _repositoryMock.Setup(r => r.Profession).Returns(_professionRepositoryMock.Object);
            _handler = new UpsertProfessionCommand.UpsertProfessionCommandHandler(_repositoryMock.Object);
        }

        [Fact]
        public async Task Handle_ShouldCreateNewProfession_WhenIdIsNull()
        {
            // Arrange
            var professionName = "Engineer";
            var command = new UpsertProfessionCommand { Name = professionName };
            _professionRepositoryMock.Setup(r => r.Persist(It.IsAny<Profession>()));

            _professionRepositoryMock
                .Setup(r => r.Persist(It.IsAny<Profession>()))
                .Callback<Profession>(p =>
                {
                    if (p.Id == 0)
                    {
                        var professionType = typeof(Profession);
                        var idProperty = professionType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
                        if (idProperty != null && idProperty.CanWrite)
                        {
                            idProperty.SetValue(p, 1); // Set the Id to 1
                        }
                    }
                });

            // Act
            var result = await _handler.Handle(command, CancellationToken.None);

            // Assert
            _professionRepositoryMock.Verify(r => r.Persist(It.Is<Profession>(p => p.Name == professionName)), Times.Once);
            result.Should().BeGreaterThan(0);
        }

        [Fact]
        public async Task Handle_ShouldUpdateExistingProfession_WhenIdIsProvided()
        {
            // Arrange

            var existingProfession = new Profession("Kitchen, assistant");

            // We use Refection to set the Id because it's protected
            var professionType = typeof(Profession);
            var idProperty = professionType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (idProperty != null && idProperty.CanWrite)
            {
                idProperty.SetValue(existingProfession, 1); // Set the Id to 1
            }

            var command = new UpsertProfessionCommand { Id = 1, Name = "Kitchen assistant" };
            _professionRepositoryMock.Setup(r => r.Get(1)).Returns(existingProfession);
            _professionRepositoryMock.Setup(r => r.Persist(It.IsAny<Profession>()));

            // Act
            var result = await _handler.Handle(command, CancellationToken.None);

            // Assert
            existingProfession.Name.Should().Be("Kitchen assistant");
            _professionRepositoryMock.Verify(r => r.Persist(existingProfession), Times.Once);
            result.Should().Be(1);
        }

        [Fact]
        public async Task Handle_ShouldThrowException_WhenProfessionNotFound()
        {
            // Arrange
            var command = new UpsertProfessionCommand { Id = 99, Name = "Astronaut" };
            _professionRepositoryMock.Setup(r => r.Get(99)).Returns((AdminAreaManagement.Core.Entities.Profession)null);

            // Act
            Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

            // Assert
            await act.Should().ThrowAsync<Exception>().WithMessage("Profession not found.");
        }

        [Fact]
        public async Task Handle_ShouldTrimName_BeforePersisting()
        {
            // Arrange
            var command = new UpsertProfessionCommand { Name = "  Trimmed Profession  " };
            _professionRepositoryMock.Setup(r => r.Persist(It.IsAny<AdminAreaManagement.Core.Entities.Profession>()));

            // Act
            var result = await _handler.Handle(command, CancellationToken.None);

            // Assert
            _professionRepositoryMock.Verify(r => r.Persist(It.Is<AdminAreaManagement.Core.Entities.Profession>(p => p.Name == "Trimmed Profession")), Times.Once);
        }
    }
}