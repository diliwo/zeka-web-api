namespace ClientManagement.Core.Common.Dto;

public class JobOccupantDto
{
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string FullName => $"{FirstName} {LastName}";
    public string ReferenceNumber { get; set; }
    public DateTime? StartOccupationDate { get; set; }
    public DateTime? EndOccupationDate { get; set; }
}