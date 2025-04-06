using AdminAreaManagement.Application.Professions.Commands.UpsertProfession;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using FluentAssertions;
using Moq;
using System.Reflection;
using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Application.Professions.Commands.DeleteProfession;
using Xunit;

namespace Application.UnitTests
{
    public class DeleteProfessionCommandHandlerTests
    {
        private readonly Mock<IRepositoryManager> _repositoryMock;
        private readonly Mock<IProfessionRepository> _professionRepositoryMock;
        private readonly DeleteProfessionCommand.DeleteProfessionCommandHandler _handler;

        public DeleteProfessionCommandHandlerTests()
        {
            _repositoryMock = new Mock<IRepositoryManager>();
            _professionRepositoryMock = new Mock<IProfessionRepository>();
            _repositoryMock.Setup(r => r.Profession).Returns(_professionRepositoryMock.Object);
            _handler = new DeleteProfessionCommand.DeleteProfessionCommandHandler(_repositoryMock.Object);
        }

        [Fact]
        public async Task Handle_ShouldSoftDeleteProfession_WhenProfessionExistsAndIsNotSoftDeleted()
        {
            // Arrange
            var profession = new Profession("Kitchen assistant");
            var professionType = typeof(Profession);
            var idProperty = professionType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (idProperty != null && idProperty.CanWrite)
            {
                idProperty.SetValue(profession, 1); // Set the Id to 1
            }

            var command = new DeleteProfessionCommand { Id = 1 };

            _professionRepositoryMock.Setup(r => r.Get(1)).Returns(profession);

            // Act
            await _handler.Handle(command, CancellationToken.None);

            // Assert
            profession.Softdelete.Should().BeTrue();
            _professionRepositoryMock.Verify(r => r.SoftDelete(profession), Times.Once);
        }

        [Fact]
        public async Task Handle_ShouldRestoreProfession_WhenProfessionExistsAndIsSoftDeleted()
        {
            // Arrange
            var profession = new Profession("Kitchen assistant");
            var professionType = typeof(Profession);
            var idProperty = professionType.GetProperty("Id", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            if (idProperty != null && idProperty.CanWrite)
            {
                idProperty.SetValue(profession, 1); // Set the Id to 1
            }


            profession.Softdelete = true;
            var command = new DeleteProfessionCommand { Id = 1 };

            _professionRepositoryMock.Setup(r => r.Get(1)).Returns(profession);

            // Act
            await _handler.Handle(command, CancellationToken.None);

            // Assert
            profession.Softdelete.Should().BeFalse();
            _professionRepositoryMock.Verify(r => r.SoftDelete(profession), Times.Once);
        }

        [Fact]
        public async Task Handle_ShouldThrowNotFoundException_WhenProfessionDoesNotExist()
        {
            // Arrange
            var command = new DeleteProfessionCommand { Id = 99 };

            _professionRepositoryMock.Setup(r => r.Get(99)).Returns((Profession)null);

            // Act
            Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

            // Assert
            await act.Should().ThrowAsync<NotFoundException>().WithMessage("Entity \"Profession\" (99) was not found.");
        }
    }

}