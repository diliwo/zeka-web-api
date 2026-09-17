using AdminAreaManagement.Core.Entities;

namespace AdminAreaManagement.Core.Interfaces
{
    public interface IDocumentPartnerRepository
    {
        DocumentPartner Persist(DocumentPartner document, Guid operationId, string requestHash);
        DocumentPartner Get(int id);
        IQueryable<DocumentPartner> GetDocuments();
        IQueryable<DocumentPartner> getDocumentsByJobIAndPartnerId(int partnerId, int jobId);
        DocumentPartner Delete(int id, Guid operationId);
    }
}