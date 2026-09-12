using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Cities.Commands.UpdateCity
{
    [AdminAreaManagement.Application.Common.Authorization.RequiresTenantPermission("Platform.ReferenceData.Manage")]
    public class UpdateCityCommand : IRequest<int>
    {
        public int CityId { get; set; }
        public string? Name { get; set; }
        public string? Country { get; set; }
        public string? CountryDenomin { get; set; }

        public class UpdateSchoolCommandHandler : IRequestHandler<UpdateCityCommand, int>
        {
            private IRepositoryManager _repository;

            public UpdateSchoolCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<int> Handle(UpdateCityCommand request, CancellationToken cancellationToken)
            {

                City entity = _repository.City.GetById((int)request.CityId);

                if (entity is null)
                {
                    throw new NotFoundException($"City with id {request.CityId} doesn't exist");
                }

                if (!String.IsNullOrEmpty(request.Name))
                {
                    entity.Name = request.Name;
                }

                if (!String.IsNullOrEmpty(request.Country))
                {
                    entity.Country = request.Country;
                }

                _repository.City.Persist(entity);

                return entity.Id;
            }
        }

    }
}
