using ClientManagement.Core.Common;

namespace ClientManagement.Core.Entities
{
    public class Language : Entity, Zeka.Extensions.MultiTenancy.Abstractions.IGlobalEntity
    {
        public string Name { get; set; }
    }
}
