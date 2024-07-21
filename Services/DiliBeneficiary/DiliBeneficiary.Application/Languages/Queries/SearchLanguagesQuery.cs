using DiliBeneficiary.Core.Interfaces;
using MediatR;

namespace DiliBeneficiary.Application.Languages.Queries
{
    public class SearchLanguagesQuery : IRequest<List<LanguageDto>>
    {
        public string SearchText = string.Empty;

        public SearchLanguagesQuery()
        {
        }

        public SearchLanguagesQuery(string searchText)
        {
            SearchText = searchText;
        }
    }

    public class SearchLanguagesQueryHandler : IRequestHandler<SearchLanguagesQuery, List<LanguageDto>>
    {
        private ILanguageRepository _repository;

        public SearchLanguagesQueryHandler(ILanguageRepository repository)
        {
            _repository = repository;
        }

        public async Task<List<LanguageDto>> Handle(SearchLanguagesQuery request, CancellationToken cancellationToken)
        {
            var result = await _repository.SearchLanguages(request.SearchText);
            return result.Select(l => new LanguageDto()
            {
                Id = l.Id,
                Name = l.Name,
            }).ToList();
        }
    }

    public class LanguageDto
    {
        public int Id { get; set; }
        public string Name { get; set; }
    }
}
