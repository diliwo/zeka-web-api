using AutoMapper;
using DiliBeneficiary.Application.Common.Mappings;
using DiliBeneficiary.Application.Supports.Queries;
using DiliBeneficiary.Core.Entities;
using DiliBeneficiary.Core.Enums;

namespace DiliBeneficiary.Application.Beneficiaries.Queries.GetBeneficiaryDetail
{
    public class BeneficiaryVm : IMapFrom<Beneficiary>
    {
        public int BeneficiaryId { get; set; }
        public string ReferenceNumber { get; set; }
        public int CivilStatus { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public int Gender { get; set; }
        public DateTime BirthDate { get; set; }
        public DateTime StartDateInCpas { get; set; }
        public DateTime? EndDateInCpas { get; set; }
        public string Nationality { get; set; }
        public string Niss { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
        public string MobilePhone { get; set; }
        public string SupportReferentName { get; set; }
        public string SupportReferentService { get; set; }
        public DateTime? SupportStartDate { get; set; }
        public DateTime? SupportEndDate { get; set; }
        public string NativeLanguage { get; set; }
        public string ContactLanguage { get; set; }
        public string Address { get; set; }
        public int Age { get; set; }
        public string SocialWorkerName { get; set; }
        public IList<SupportDto> Supports { get; set; } = new List<SupportDto>();
        //public IEnumerable<CandidacyDto> Candidacies { get; set; }
        public ContratPiisDto ContratPiis { get; set; }
        public string? LastFormationName { get; set; }
        public string? LastFormationNote { get; set; }
        public int? LastFormationResult { get; set; }
        public DateTime? LastFormationStartDate { get; set; }
        public DateTime? LastFormationEndDate { get; set; }
        public string? LastJobExperienceCompanyName { get; set; }
        public TypeOfContract? LastJobExperienceContractTypeName { get; set; }
        public string? LastJobExperienceFunction { get; set; }
        public DateTime? LastJobExperienceStartDate { get; set; }
        public DateTime? LastJobExperienceEndDate { get; set; }
        public DateTime? LastEvaluationDate { get; set; }
        public string IbisNumber { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<Beneficiary, BeneficiaryVm>()
                .ForMember(b => b.CivilStatus, opt => opt.MapFrom(s => (int)s.CivilStatus))
                .ForMember(b => b.Gender, opt => opt.MapFrom(e => (int)e.Gender))
                .ForMember(b => b.BeneficiaryId, opt => opt.MapFrom(e => e.Id))
                .ForMember(b => b.Address,
                    opt =>
                        opt.MapFrom(e =>
                            !String.IsNullOrEmpty(e.Address.Street)
                                ? e.Address.Street + (e.Address.Number != "0" ? " " + e.Address.Number + ", " : "") +
                                  (e.Address.PostalCode != "0" ? e.Address.PostalCode + " " : "") +
                                  e.Address.City
                                : "Néant"))
                .ForMember(b => b.NativeLanguage, opt => opt.MapFrom(e => e.NativeLanguage.SpokenLanguage))
                .ForMember(b => b.ContactLanguage, opt => opt.MapFrom(e => e.ContactLanguage.SpokenLanguage))
                .ForMember(b => b.SupportReferentName,
                    opt => opt.MapFrom(e =>
                        e.Supports.Where(b => b.Softdelete != true).Count() > 0
                            ? e.Supports.Where(b => b.Softdelete != true).OrderBy(d => d.Created).Last().Referent
                                .FullName
                            : string.Empty))
                .ForMember(b => b.SupportReferentService,
                    opt => opt.MapFrom(e =>
                        e.Supports.Where(b => b.Softdelete != true).Count() > 0
                            ? ("(" + e.Supports.Where(b => b.Softdelete != true).OrderBy(d => d.Created).Last().Referent
                                .Service.Acronym + ")")
                            : string.Empty))
                .ForMember(b => b.SupportStartDate,
                    opt => opt.MapFrom(
                        e => e.Supports.Where(b => b.Softdelete != true).OrderBy(d => d.Created).First().StartDate))
                .ForMember(b => b.SupportEndDate,
                    opt => opt.MapFrom(e =>
                        e.Supports.Where(b => b.Softdelete != true).OrderBy(d => d.Created).Last().EndDate))
                //.ForMember(c => c.Candidacies,
                //    opt => opt.MapFrom(e => e.Candidacies.Where(c => c.Softdelete != true)))
                .ForMember(b => b.Phone,
                    opt => opt.MapFrom(e => e.Phone.PhoneNumber))
                .ForMember(b => b.MobilePhone,
                    opt => opt.MapFrom(e => e.MobilePhone.PhoneNumber))
                .ForMember(b => b.Email, opt => opt.MapFrom(e => e.Email.EmailAddress))
                .ForMember(b => b.LastFormationName,
                    opt =>
                        opt.MapFrom(e =>
                            e.SchoolRegistrations.Where(s => s.Softdelete != true).Where(s => s.Softdelete != true)
                                .OrderBy(e => e.Created).Last().Formation.Name))
                .ForMember(b => b.LastFormationNote,
                    opt =>
                        opt.MapFrom(e =>
                            e.SchoolRegistrations.Where(s => s.Softdelete != true).OrderBy(e => e.Created).Last().Note))
                .ForMember(b => b.LastFormationResult,
                    opt =>
                        opt.MapFrom(e =>
                            (int)e.SchoolRegistrations.Where(s => s.Softdelete != true).OrderBy(e => e.Created).Last()
                                .Result))
                .ForMember(b => b.LastFormationStartDate,
                    opt =>
                        opt.MapFrom(e =>
                            e.SchoolRegistrations.Where(s => s.Softdelete != true).OrderBy(e => e.Created).Last()
                                .StartDate))
                .ForMember(b => b.LastFormationEndDate,
                    opt =>
                        opt.MapFrom(e =>
                            e.SchoolRegistrations.Where(s => s.Softdelete != true).OrderBy(e => e.Created).Last()
                                .EnDate))
                .ForMember(b => b.LastJobExperienceCompanyName,
                    opt => opt.MapFrom(e =>
                        e.ProfessionnalExpectations.Where(p => p.Softdelete != null).OrderBy(e => e.StartDate).Last()
                            .CompanyName))
                .ForMember(b => b.LastJobExperienceContractTypeName,
                    opt => opt.MapFrom(e =>
                        e.ProfessionnalExpectations.Where(p => p.Softdelete != null).OrderBy(e => e.StartDate).Last()
                            .TypeOfContract))
                .ForMember(b => b.LastJobExperienceFunction,
                    opt => opt.MapFrom(e =>
                        e.ProfessionnalExpectations.Where(p => p.Softdelete != null).OrderBy(e => e.StartDate).Last()
                            .Function))
                .ForMember(b => b.LastJobExperienceStartDate,
                    opt => opt.MapFrom(e =>
                        e.ProfessionnalExpectations.Where(p => p.Softdelete != null).OrderBy(e => e.StartDate).Last()
                            .StartDate))
                .ForMember(b => b.LastJobExperienceEndDate,
                    opt => opt.MapFrom(e =>
                        e.ProfessionnalExpectations.Where(p => p.Softdelete != null).OrderBy(e => e.StartDate).Last()
                            .EndDate));
        }
    }
}
