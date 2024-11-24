namespace Client.Core.Common.Dto
{
    public class IndividuDto
    {
        public int Id { get; set; }
        public string Niss { get; set; }
        public int IdSociabili { get; set; }
        public string Lastname { get; set; }
        public string Firstname { get; set; }
        public string NumDossier { get; set; }
        public bool SoftDeleted { get; set; }
        public string PhoneNumber { get; set; }
        public string MobilePhoneNumber { get; set; }
        public string Email { get; set; }
        public int MenageId { get; set; }
        public int SexeId { get; set; }
        public int NationalityId { get; set;}
        public int CivilStatusId { get; set;}
        public int LanguageId { get; set; }

    }
}
