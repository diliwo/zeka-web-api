namespace DiliBeneficiary.Core.Common.Dto
{
    public class AgendaDto
    {

        public int Id { get; set; }

        public string Name { get; set; }

        public string HolderLogin { get; set; }

        public int DepartmentId { get; set; }

        public List<string> CoHoldersLogin { get; set; }

        public DateTime CreatedDate { get; set; }

        public DateTime LastModifiedDate { get; set; }
        public bool Enabled { get; set; }
    }
}