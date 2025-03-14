﻿using AutoMapper;
using ClientManagement.Application.Common.Mappings;
using ClientManagement.Application.Supports.Queries;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Enums;

namespace ClientManagement.Application.Clients.Queries.GetClientDetail
{
    public class ClientDto : IMapFrom<Client>
    {
        public int ClientId { get; set; }
        public string ReferenceNumber { get; set; }
        public int CivilStatus { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public int Gender { get; set; }
        public DateTime BirthDate { get; set; }
        public string PlaceOfBirth { get; set; }
        public string Nationality { get; set; }
        public string Niss { get; set; }
        public string Email { get; set; }
        public string Phone { get; set; }
        public string MobilePhone { get; set; }
        public string SupportStaffMemberName { get; set; }
        public string SupportStaffMemberService { get; set; }
        public DateTime? SupportStartDate { get; set; }
        public DateTime? SupportEndDate { get; set; }
        public string NativeLanguage { get; set; }
        public string ContactLanguage { get; set; }
        public string Address { get; set; }
        public int Age { get; set; }
        public string SocialWorkerName { get; set; }
        public IList<SupportDto> Supports { get; set; } = new List<SupportDto>();
        //public IEnumerable<CandidacyDto> Candidacies { get; set; }
        public string? LastTrainingName { get; set; }
        public string? LastTrainingNote { get; set; }
        public int? LastTrainingResult { get; set; }
        public DateTime? LastTrainingStartDate { get; set; }
        public DateTime? LastTrainingEndDate { get; set; }
        public string? LastJobExperienceCompanyName { get; set; }
        public TypeOfContract? LastJobExperienceContractTypeName { get; set; }
        public string? LastJobExperienceFunction { get; set; }
        public DateTime? LastJobExperienceStartDate { get; set; }
        public DateTime? LastJobExperienceEndDate { get; set; }

        public void Mapping(Profile profile)
        {
            profile.CreateMap<Client, ClientDto>()
                .ForMember(b => b.CivilStatus, opt => opt.MapFrom(s => (int)s.CivilStatus))
                .ForMember(b => b.Gender, opt => opt.MapFrom(e => (int)e.Gender))
                .ForMember(b => b.ClientId, opt => opt.MapFrom(e => e.Id))
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
                .ForMember(b => b.SupportStaffMemberName,
                    opt => opt.MapFrom(e =>
                        e.Supports.Where(b => b.Softdelete != true).Count() > 0
                            ? e.Supports.Where(b => b.Softdelete != true).OrderBy(d => d.Created).Last().SocialWorker
                                .FullName
                            : string.Empty))
                .ForMember(b => b.SupportStaffMemberService,
                    opt => opt.MapFrom(e =>
                        e.Supports.Where(b => b.Softdelete != true).Count() > 0
                            ? ("(" + e.Supports.Where(b => b.Softdelete != true).OrderBy(d => d.Created).Last().SocialWorker
                                .TeamAcronym + ")")
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
                .ForMember(b => b.LastTrainingName,
                    opt =>
                        opt.MapFrom(e =>
                            e.SchoolRegistrations.Where(s => s.Softdelete != true).Where(s => s.Softdelete != true)
                                .OrderBy(e => e.Created).Last().Training.Name))
                .ForMember(b => b.LastTrainingNote,
                    opt =>
                        opt.MapFrom(e =>
                            e.SchoolRegistrations.Where(s => s.Softdelete != true).OrderBy(e => e.Created).Last().Note))
                .ForMember(b => b.LastTrainingResult,
                    opt =>
                        opt.MapFrom(e =>
                            (int)e.SchoolRegistrations.Where(s => s.Softdelete != true).OrderBy(e => e.Created).Last()
                                .Result))
                .ForMember(b => b.LastTrainingStartDate,
                    opt =>
                        opt.MapFrom(e =>
                            e.SchoolRegistrations.Where(s => s.Softdelete != true).OrderBy(e => e.Created).Last()
                                .StartDate))
                .ForMember(b => b.LastTrainingEndDate,
                    opt =>
                        opt.MapFrom(e =>
                            e.SchoolRegistrations.Where(s => s.Softdelete != true).OrderBy(e => e.Created).Last()
                                .EnDate));

        }
    }
}
