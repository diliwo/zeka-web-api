﻿using AutoMapper;
using ClientManagement.Application.Clients.Commands.Exceptions;
using ClientManagement.Application.Clients.Model;
using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Core.Interfaces;
using MediatR;
using Microsoft.AspNetCore.JsonPatch;

namespace ClientManagement.Application.Clients.Commands.UpSertIbisNumber
{
    public class UpSertIbisNumberCommand : IRequest<UpdateClientDto>
    {
        public string Niss { get; set; }
        public JsonPatchDocument<UpdateClientDto> PatchDoc { get; set; }
        public class UpSertIbisNumberCommandHandler : IRequestHandler<UpSertIbisNumberCommand, UpdateClientDto>
        {
            private readonly IRepositoryManager _repository;
            private readonly IMapper _mapper;

            public UpSertIbisNumberCommandHandler(IRepositoryManager repository, IMapper mapper)
            {
                _repository = repository;
                _mapper = mapper;
            }

            public async Task<UpdateClientDto> Handle(UpSertIbisNumberCommand request, CancellationToken cancellationToken)
            {
                if (request.PatchDoc is null)
                {
                    throw new PatchDocBadRequestException();
                }

                var entity = await _repository.Client.GetClientByNissAsync(request.Niss, true);

                if (entity is null)
                {
                    throw new NotFoundException(nameof(entity));
                }

                var beneficaryToPatch = _mapper.Map<UpdateClientDto>(entity);

                request.PatchDoc.ApplyTo(beneficaryToPatch);

                _mapper.Map(beneficaryToPatch, entity);

                _repository.Client.Persist(entity);

                _repository.Save();

                return beneficaryToPatch;
            }
        }
    }
}
