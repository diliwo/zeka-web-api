using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Cities.Commands.CreateCity
{
    public class CreateCityCommand : IRequest<int>
    {
        public string Name { get; set; }
        public string Country { get; set; }

        public class CreateCityCommandHandler : IRequestHandler<CreateCityCommand, int>
        {
            private IRepositoryManager _repository;

            public CreateCityCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<int> Handle(CreateCityCommand request, CancellationToken cancellationToken)
            {
                try
                {
                    City entity = new City(request.Name, request.Country);

                    _repository.City.Persist(entity);

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