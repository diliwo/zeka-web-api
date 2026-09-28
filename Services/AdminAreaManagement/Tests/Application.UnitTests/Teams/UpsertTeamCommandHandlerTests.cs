using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Application.Teams.Commands.UpsertTeam;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using Moq;
using Xunit;

namespace Application.UnitTests.Teams;

public sealed class UpsertTeamCommandHandlerTests
{
    [Fact]
    public async Task Unresolved_persisted_identity_is_not_found_without_persist_or_save()
    {
        var teams = new Mock<ITeamRepository>(MockBehavior.Strict);
        teams.Setup(repository => repository.Get(42)).Returns((Team)null!);
        var repository = new Mock<IRepositoryManager>(MockBehavior.Strict);
        repository.SetupGet(manager => manager.Team).Returns(teams.Object);
        var handler = new UpsertTeamCommand.UpsertTeamCommandHandler(repository.Object);

        var exception = await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(
            new UpsertTeamCommand { Id = 42, Name = "Unresolved", Acronym = "UNR" }, default));

        Assert.Equal("Entity \"Team\" (42) was not found.", exception.Message);
        teams.Verify(port => port.Get(42), Times.Once);
        teams.Verify(port => port.Persist(It.IsAny<Team>()), Times.Never);
        repository.Verify(port => port.Save(), Times.Never);
        repository.Verify(port => port.SaveAsync(), Times.Never);
        teams.VerifyNoOtherCalls();
    }
}
