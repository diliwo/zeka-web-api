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

        public void Persist(DocumentPartner document)
        {
            // Check if document has fileName
            if (document.Name != null && document.Name.Length < 1)
            {
                throw new InvalidDataException(
                    "Erreur lors de la tentative d'enregistrement du document : Il n'existe aucun nom de fichier !");
            }

            // The operation-level tenant transaction executor owns the transaction. A nested transaction would
            // bypass retry/context initialization. Production composition queues file persistence until the
            // database commit is acknowledged, keeping it outside execution-strategy retries.
            _context.DocumentPartners.Add(document);
            _context.SaveChanges();
            if (document.Id == default)
                throw new InvalidOperationException($"The document id {document.Id} is not correct !");
            void Save() => _fileService.SaveFile(document.Id, document.PartnerId, document.Name,
                document.ContentFile, document.ContentType);
            if (_postCommit is null) Save(); else _postCommit.Enqueue(Save);
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
        public void Delete(int id)
        {
            var doc = Get(id);
            if (doc == null)
            {
                throw new InvalidDataException(
                    "Ce document n'existe pas!");
            }
            try
            {
                doc.Softdelete = true;
                _context.SaveChanges();
                void Delete() => _fileService.DeleteFile(doc.Id, doc.PartnerId, doc.ContentType);
                if (_postCommit is null) Delete(); else _postCommit.Enqueue(Delete);
            }
            catch (Exception ex)
            {
                throw new ApplicationException(ex.Message);
            }
        }

    }
}
