using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Nationalities.Commands.CreateCity
{
    public class CreateNationalityCommand : IRequest<int>
    {
        public string Name { get; set; }

        public class CreateCityCommandHandler : IRequestHandler<CreateNationalityCommand, int>
        {
            private IRepositoryManager _repository;

            public CreateCityCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<int> Handle(CreateNationalityCommand request, CancellationToken cancellationToken)
            {
                try
                {
                    Nationality entity = new Nationality(request.Name);

                    _repository.Nationality.Persist(entity);

                    _repository.Save();

                    return entity.Id;
                }
                catch (Exception ex)
                {
                    throw ex;
                }
            }
        }

    }
}