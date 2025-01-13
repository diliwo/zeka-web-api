using ClientManagement.Core.Common;
using ClientManagement.Core.Enums;

namespace ClientManagement.Core.Entities
{
    public class ProfessionnalExperience : Entity
    {
        public DateTime StartDate { get; set; }
        public DateTime EndDate { get; set; }
        public string CompanyName { get; set; }
        public string Function { get; set; }
        public string Task { get; set; }
        public string Environment { get; set; }
        public string ContextOfHiring { get; set; }
        public TypeOfContract TypeOfContract {get; set; }
        public NatureOfContract NatureOfContract { get; set; }
        public int NatureOfContractId { get; set; }
        public string ReasonEndOfContract { get; set; }
        public Client Client { get; set; }
        public int ClientId { get; set; }
        public ProfessionnalExperience() { }
        public ProfessionnalExperience(
            DateTime startDate,
            DateTime endDate,
            string companyName, 
            string function,
            string task,
            string environment,
            string contextOfHiring, 
            NatureOfContract natureOfContract,
            string reasonEndOfContract,
            Client client
            )
        {
            StartDate = startDate;
            EndDate = endDate;
            CompanyName = companyName;
            Function = function;
            Task = task;
            Environment = environment;
            ContextOfHiring = contextOfHiring;
            NatureOfContract = natureOfContract;
            ReasonEndOfContract = reasonEndOfContract;
            Client = client;
        }
    }
}
