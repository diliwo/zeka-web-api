﻿using ClientManagement.Application.Common.Exceptions;
using ClientManagement.Core.Interfaces;
using MediatR;

namespace ClientManagement.Application.Clients.Commands.UpdateNativeLanguage
{
    public class UpdateNativeLanguageCommand : IRequest<int>
    {
        //public int Id { get; set; }
        public string Niss { get; set; }
        public string Language { get; set; }

        public class UpdateNativeLanguageCommandHandler : IRequestHandler<UpdateNativeLanguageCommand, int>
        {
            private readonly IRepositoryManager _repository;

            public UpdateNativeLanguageCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task<int> Handle(UpdateNativeLanguageCommand request, CancellationToken cancellationToken)
            {
                Console.WriteLine(request.Niss);

                var entity = _repository.Client.GetClientBySsN(request.Niss);

                if (entity == null)
                {
                    throw new NotFoundException(nameof(entity));
                }


                entity.UpdateNativeLanguage(request.Language);

                _repository.Client.Persist(entity);

                _repository.SaveAsync();

                return entity.Id;
            }
        }
    }
}
