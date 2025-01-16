using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Application.DocumentPartners.Commands.Persist;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using Moq;
using Xunit;

namespace Application.UnitTests;

public class DocumentPartnersTest
{
    [Fact]
    public async Task Handle_ShouldThrowNotFoundException_WhenPartnerNotFound()
    {
        // Arrange
        var mockRepositoryManager = new Mock<IRepositoryManager>();
        var mockPartnerRepository = new Mock<IPartnerRepository>();

        mockPartnerRepository.Setup(repo => repo.Get(It.IsAny<int>())).Returns((Partner)null);

        mockRepositoryManager.Setup(repo => repo.Partner).Returns(mockPartnerRepository.Object);

        var handler = new PersistDocumentPartnerCommand.PersistDocumentPartnerCommandHandler(mockRepositoryManager.Object);
        var command = new PersistDocumentPartnerCommand
        {
            PartnerId = 1
        };

        // Act & Assert
        await Assert.ThrowsAsync<NotFoundException>(() => handler.Handle(command, CancellationToken.None));
        mockRepositoryManager.Verify(repo => repo.Save(), Times.Never);
    }

    [Fact]
    public async Task Handle_ShouldPropagateException_WhenRepositoryFails()
    {
        // Arrange
        var mockRepositoryManager = new Mock<IRepositoryManager>();
        var mockPartnerRepository = new Mock<IPartnerRepository>();
        var mockDocumentPartnerRepository = new Mock<IDocumentPartnerRepository>();

        var partner = new Partner { Name = "Test Partner" };

        mockPartnerRepository.Setup(repo => repo.Get(1)).Returns(partner);
        mockDocumentPartnerRepository
            .Setup(repo => repo.Persist(It.IsAny<DocumentPartner>()))
            .Throws(new Exception("Persistence Error"));

        mockRepositoryManager.Setup(repo => repo.Partner).Returns(mockPartnerRepository.Object);
        mockRepositoryManager.Setup(repo => repo.DocumentPartner).Returns(mockDocumentPartnerRepository.Object);

        var handler = new PersistDocumentPartnerCommand.PersistDocumentPartnerCommandHandler(mockRepositoryManager.Object);
        var command = new PersistDocumentPartnerCommand
        {
            PartnerId = 1,
            Name = "Test Document",
            Description = "Test Description",
            ContentType = "application/pdf",
            ContentFile = new byte[] { 0x01, 0x02 }
        };

        // Act & Assert
        var exception = await Assert.ThrowsAsync<Exception>(() => handler.Handle(command, CancellationToken.None));
        Assert.Equal("Persistence Error", exception.Message);
    }
}