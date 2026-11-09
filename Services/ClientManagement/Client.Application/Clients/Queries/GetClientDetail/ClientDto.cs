using ClientManagement.Application.Supports.Queries;
using ClientManagement.Core.Enums;
using ClientManagement.Core.ValueObjects;

namespace ClientManagement.Application.Clients.Queries.GetClientDetail
{
    public class ClientDto
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
        public Address Address { get; set; }
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
    }
}
