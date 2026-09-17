using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;

namespace AdminAreaManagement.Infrastructure.Persistence
{
    public class DocumentPartnerRepository : IDocumentPartnerRepository
    {
        private readonly ApplicationDbContext _context;
        private readonly IFileService _fileService;
        private readonly TenantPostCommitActions? _postCommit;

        public DocumentPartnerRepository() { }

        public DocumentPartnerRepository(ApplicationDbContext context, IFileService fileService)
            : this(context, fileService, null)
        {
        }

        public DocumentPartnerRepository(ApplicationDbContext context, IFileService fileService,
            TenantPostCommitActions? postCommit)
        {
            _context = context;
            _fileService = fileService;
            _postCommit = postCommit;
        }

        public DocumentPartner Persist(DocumentPartner document, Guid operationId, string requestHash)
        {
            // Check if document has fileName
            if (document.Name != null && document.Name.Length < 1)
            {
                throw new InvalidDataException(
                    "Erreur lors de la tentative d'enregistrement du document : Il n'existe aucun nom de fichier !");
            }

            var existing = _context.DocumentPartners.SingleOrDefault(x => x.CreateOperationId == operationId);
            if (existing is not null)
            {
                if (!StringComparer.Ordinal.Equals(existing.CreateRequestHash, requestHash))
                    throw new InvalidOperationException("The document operation identity was reused for different content.");
                if (existing.FileWriteState != DocumentFileOperationState.Completed) QueueWrite(existing);
                return existing;
            }

            document.BeginFileWrite(operationId, requestHash, document.ContentFile);
            _context.DocumentPartners.Add(document);
            _context.SaveChanges();
            if (document.Id == default)
                throw new InvalidOperationException($"The document id {document.Id} is not correct !");
            QueueWrite(document);
            return document;
        }

        public DocumentPartner Get(int id)
        {
            return _context.DocumentPartners.SingleOrDefault(d => d.Id == id);
        }

        public IQueryable<DocumentPartner> GetDocuments()
        {
            return _context.DocumentPartners.Where(d => d.Softdelete != true);
        }

        public IQueryable<DocumentPartner> getDocumentsByJobIAndPartnerId(int partnerId, int jobId)
        {
            return _context.DocumentPartners.Where(d =>
                d.PartnerId == partnerId && d.Softdelete != true);
        }
        public DocumentPartner Delete(int id, Guid operationId)
        {
            var existing = _context.DocumentPartners.SingleOrDefault(x => x.DeleteOperationId == operationId);
            if (existing is not null)
            {
                if (existing.Id != id)
                    throw new InvalidOperationException("The document operation identity was reused for a different document.");
                if (existing.FileDeleteState != DocumentFileOperationState.Completed) QueueDelete(existing);
                return existing;
            }
            var doc = Get(id);
            if (doc == null)
            {
                throw new InvalidDataException(
                    "Ce document n'existe pas!");
            }
            try
            {
                doc.BeginFileDelete(operationId);
                doc.Softdelete = true;
                _context.SaveChanges();
                QueueDelete(doc);
                return doc;
            }
            catch (Exception ex)
            {
                throw new ApplicationException(ex.Message);
            }
        }

        private void QueueWrite(DocumentPartner document)
        {
            if (_postCommit is null)
                throw new InvalidOperationException("Durable document delivery requires the tenant post-commit executor.");
            var content = document.PendingFileContent?.ToArray()
                ?? throw new InvalidOperationException("Pending document content is missing.");
            _postCommit.Enqueue(
                () => _fileService.SaveFile(document.OrganisationId, document.Id, document.PartnerId, content),
                () => _context.Update(document),
                document.CompleteFileWrite, document.FailFileWrite, document.PendFileWrite);
        }

        private void QueueDelete(DocumentPartner document)
        {
            if (_postCommit is null)
                throw new InvalidOperationException("Durable document delivery requires the tenant post-commit executor.");
            _postCommit.Enqueue(
                () => _fileService.DeleteFile(document.OrganisationId, document.Id, document.PartnerId),
                () => _context.Update(document),
                document.CompleteFileDelete, document.FailFileDelete, document.PendFileDelete);
        }

    }
}
