using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Application.Common.Models;
using AdminAreaManagement.Core.Interfaces;
using AutoMapper;
using MediatR;

namespace AdminAreaManagement.Application.DocumentPartners.Queries.GetDocument
{
    public class GetSelectedDocumentQuery : IRequest<FileDto>
    {
        public int PartnerId { get; set; }
        public int JobId { get; set; }
        public int DocumentId { get; set; }

        public class  GetSelectedDocumentQueryHandler : IRequestHandler<GetSelectedDocumentQuery, FileDto>
        {
            private readonly IRepositoryManager _repository;
            private readonly IFileService _fileService;
            private readonly IMapper _mapper;

            public GetSelectedDocumentQueryHandler(IRepositoryManager repository, IFileService contextFile, IMapper mapper)
            {
                _repository = repository;
                _fileService = contextFile;
                _mapper = mapper;
            }

            public async Task<FileDto> Handle(GetSelectedDocumentQuery query, CancellationToken cancellationToken)
            {
                var document = _repository.DocumentPartner.Get(query.DocumentId);

                if (document == null)
                    throw new NotFoundException("Document", query.DocumentId);

                try
                {
                    return new FileDto()
                    {
                        Name = document.Description,
                        Data = _fileService.GetContentFile(query.PartnerId, query.JobId, query.DocumentId,document.ContentType)
                    };
                }
                catch (Exception ex)
                {
                    var filePath = _fileService.GetFolderPath(query.PartnerId);
                    Log.Error(ex,$"GetSelectedDocumentQueryHandler {query.DocumentId}");
                    if (ex.GetType() == typeof(FileNotFoundException) || ex.GetType() == typeof(DirectoryNotFoundException))
                        throw new NotFoundException("Fichier", filePath);
                    throw new FileAccessException(filePath);
                }
            }
        }
    }
}