using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;

namespace AdminAreaManagement.Infrastructure.Persistence
{
    public class DocumentPartnerRepository : IDocumentPartnerRepository
    {
        private readonly ApplicationDbContext _context;
        private readonly IFileService _fileService;

        public DocumentPartnerRepository() { }

        public DocumentPartnerRepository(ApplicationDbContext context, IFileService fileService)
        {
            _context = context;
            _fileService = fileService;
        }

        public void Persist(DocumentPartner document)
        {
            // Check if document has fileName
            if (document.Name != null && document.Name.Length < 1)
            {
                throw new InvalidDataException(
                    "Erreur lors de la tentative d'enregistrement du document : Il n'existe aucun nom de fichier !");
            }

            using (var transaction = _context.Database.BeginTransaction())
            {
                _context.DocumentPartners.Add(document);

                _context.SaveChanges();

                try
                {
                    if (document.Id != default)
                    {
                        _fileService.SaveFile(document.Id, document.PartnerId, document.Name, document.ContentFile, document.ContentType);
                    }
                    else
                    {
                        transaction.Rollback();
                        throw new InvalidOperationException($"The document id {document.Id} is not correct !");
                    }
                }
                catch (Exception e)
                {
                    transaction.Rollback();
                    throw;
                }
                transaction.Commit();
            }
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
                _fileService.DeleteFile(doc.Id, doc.PartnerId, doc.ContentType);
            }
            catch (Exception ex)
            {
                throw new ApplicationException(ex.Message);
            }
        }

    }
}
