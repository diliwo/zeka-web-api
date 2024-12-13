using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Enums;
using AdminAreaManagement.Core.Interfaces;
using AdminAreaManagement.Core.ValueObjects;
using MediatR;

namespace AdminAreaManagement.Application.Partners.Commands.UpdatePartner
{
    public class UpdatePartnerCommand : IRequest<int>
    {
        public int? PartnerId { get; set; }
        public int PartnerNumber { get; set; }
        public string Name { get; set; }
        public string StreetCity { get; set; }
        public string StreetName { get; set; }
        public string BoxNumber { get; set; }
        public string StreetNumber { get; set; }
        public string StreetPostalCode { get; set; }
        public List<ContactPerson> ContactPersons { get; set; }
        public List<Email> Emails { get; set; }
        public int JobCoachId { get; set; }
        public bool? IsEconomieSociale { get; set; }
        public CategoryOfPartner CategoryOfPartner { get; set; }
        public StatusOfPartner StatusOfPartner { get; set; }
        public DateTime DateOfAgreementSignature { get; set; }
        public DateTime? DateOfConclusion { get; set; }
        public string Note { get; set; }

        public class UpdatePartnerCommandHandler : IRequestHandler<UpdatePartnerCommand, int>
        {
            private readonly IRepositoryManager _repository;

            public UpdatePartnerCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task<int> Handle(UpdatePartnerCommand request, CancellationToken cancellationToken)
            {
                Partner entity;

                if (!request.PartnerId.HasValue)
                {
                    throw new NotFoundException(nameof(Partner), request.PartnerId);
                }

                entity = _repository.Partner.Get(request.PartnerId.Value);
                entity.PartnerNumber = request.PartnerNumber;
                entity.Name = request.Name;
                entity.Address.Street = request.StreetName;
                entity.Address.Number = request.StreetNumber;
                entity.Address.City = request.StreetCity;
                entity.Address.PostalCode = request.StreetPostalCode;
                entity.CategoryOfPartner = request.CategoryOfPartner;
                entity.CategoryOfPartnerName = Enum.GetName(request.CategoryOfPartner);
                entity.StatusOfPartner = request.StatusOfPartner;
                entity.DateOfAgreementSignature = request.DateOfAgreementSignature.ToLocalTime();
                entity.IsEconomieSociale = request.IsEconomieSociale;
                entity.Note = request.Note;

                if (request.ContactPersons != null)
                {
                    foreach (var p in request.ContactPersons)
                    {
                        entity.AssignContactPerson(new ContactPerson(p.ContactDetails, p.ContactName, p.Gender));
                    }

                    entity.removeContactPerson(request.ContactPersons);
                }

                if (request.DateOfConclusion.HasValue)
                {
                    entity.DateOfConclusion = request.DateOfConclusion.Value.ToLocalTime();
                }

                _repository.Partner.Persist(entity);

                _repository.Save();

                return entity.Id;
            }
        }

    }
}
