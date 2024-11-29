using AdminAreaManagement.Application.Common.Exceptions;
using AdminAreaManagement.Core.Entities;
using AdminAreaManagement.Core.Enums;
using AdminAreaManagement.Core.Interfaces;
using AdminAreaManagement.Core.ValueObjects;
using MediatR;

namespace AdminAreaManagement.Application.Partners.Commands.CreatePartner
{
    public class CreatePartnerCommand : IRequest<int>
    {
        public int PartnerNumber { get; set; }
        public string Name { get; set; }
        public string StreetCity { get; set; }
        public string StreetName { get; set; }
        public string BoxNumber { get; set; }
        public string StreetNumber { get; set; }
        public string StreetPostalCode { get; set; }
        public List<Phone> Phones { get; set; }
        public List<Email> Emails { get; set; }
        public int JobCoachId { get; set; }
        public bool? IsEconomieSociale { get; set; }
        public CategoryOfPartner CategoryOfPartner { get; set; }
        public StatusOfPartner StatusOfPartner { get; set; }
        public DateTime DateOfAgreementSignature { get; set; }
        public DateTime? DateOfConclusion { get; set; }
        public string Note { get; set; }

        public class CreatePartnerCommandHandler : IRequestHandler<CreatePartnerCommand, int>
        {
            private readonly IRepositoryManager _repository;

            public CreatePartnerCommandHandler(IRepositoryManager repository)
            {
                _repository = repository;
            }

            public async Task<int> Handle(CreatePartnerCommand request, CancellationToken cancellationToken)
            {
                Partner entity;

                var foundedStaffMemberContact = _repository.StaffMember.Get(request.JobCoachId);

                if (foundedStaffMemberContact == null)
                {
                    throw new NotFoundException(nameof(foundedStaffMemberContact), request.JobCoachId);
                }

                entity = new Partner(
                    request.PartnerNumber,
                    request.Name,
                    new Address(request.StreetNumber, request.StreetName, request.BoxNumber, request.StreetPostalCode, request.StreetCity),
                    foundedStaffMemberContact,
                    request.CategoryOfPartner,
                    request.StatusOfPartner,
                    request.DateOfAgreementSignature.ToLocalTime(),
                    request.IsEconomieSociale,
                    request.Note
                );

                entity.CategoryOfPartnerName = Enum.GetName(request.CategoryOfPartner);

                if (request.Phones != null)
                {
                    foreach (var p in request.Phones)
                    {
                        entity.AssignPhone(new Phone(p.PhoneNumber, p.ContactName, p.Gender));
                    }

                    entity.removePhone(request.Phones);
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
