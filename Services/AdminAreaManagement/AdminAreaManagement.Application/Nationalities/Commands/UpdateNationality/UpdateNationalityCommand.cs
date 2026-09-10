using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Interfaces;
using MediatR;

namespace AdminAreaManagement.Application.Nationalities.Commands.UpdateCity
{
    [AdminAreaManagement.Application.Common.Authorization.RequiresTenantPermission("Platform.ReferenceData.Manage")]
    public class UpdateNationalityCommand : IRequest<int>
    {
        public int NationalityId { get; set; }
        public string Name { get; set; }
        public string Country { get; set; }
        public string CountryDenomin { get; set; }

        public class UpdateNationalityCommandHandler : IRequestHandler<UpdateNationalityCommand, int>
        {
            private IRepositoryManager _repository;

            public UpdateNationalityCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }
            public async Task<int> Handle(UpdateNationalityCommand request, CancellationToken cancellationToken)
            {

                City entity = _repository.City.GetById((int)request.NationalityId);

                if (entity is null)
                {
                    throw new NotFoundException($"Nationality with id {request.NationalityId} doesn't exist");
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
