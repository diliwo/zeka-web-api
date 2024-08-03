using DiliBeneficiary.Core.Common.Dto;
using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Interfaces;

namespace DiliBeneficiary.Infrastructure.Services;

public class BeneficiaryService : IBeneficiaryService
{
    public readonly IRepositoryManager _repository;

    public BeneficiaryService(
        IRepositoryManager repository
        )
    {
        _repository = repository;
    }

    public async Task<int> Update(List<string> nisses)
    {
        return 0;
    }

    public async Task<int> UpSert(string niss)
    {
        return 0;
    }
}