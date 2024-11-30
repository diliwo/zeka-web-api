using AdminAreaManagement.Core.Common;

namespace AdminAreaManagement.Core.Entities
{
    public class DocumentPartner : Document
    {
        public Partner? Partner { get; set; }
        public int PartnerId { get; set; }
        public DocumentPartner(){}
    }
}
