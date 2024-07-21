using AutoMapper;
using DiliBeneficiary.Application.Beneficiaries.Commands.Exceptions;
using DiliBeneficiary.Application.Beneficiaries.Model;
using DiliBeneficiary.Application.Common.Exceptions;
using DiliBeneficiary.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.JsonPatch;

namespace DiliBeneficiary.Application.Beneficiaries.Commands.UpSertIbisNumber
{
    public class UpSertIbisNumberCommand : IRequest<UpdateBeneficaryDto>
    {
        public string Niss { get; set; }
        public JsonPatchDocument<UpdateBeneficaryDto> PatchDoc { get; set; }
        public class UpSertIbisNumberCommandHandler : IRequestHandler<UpSertIbisNumberCommand, UpdateBeneficaryDto>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public UpSertIbisNumberCommandHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<UpdateBeneficaryDto> Handle(UpSertIbisNumberCommand request, CancellationToken cancellationToken)
            {
                if (request.PatchDoc is null)
                {
                    throw new PatchDocBadRequestException();
                }

                var entity = await _repository.Beneficiary.GetBeneficiaryByNissAsync(request.Niss, true);

                if (entity is null)
                {
                    throw new NotFoundException(nameof(entity));
                }

                var beneficaryToPatch = _mapper.Map<UpdateBeneficaryDto>(entity);

                request.PatchDoc.ApplyTo(beneficaryToPatch);

                _mapper.Map(beneficaryToPatch, entity);

                _repository.Beneficiary.Persist(entity);

                _repository.Save();

                return beneficaryToPatch;
            }
        }
    }
}
