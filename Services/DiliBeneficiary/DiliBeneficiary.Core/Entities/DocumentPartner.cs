namespace DiliBeneficiary.Core.Entities
{
    public class DocumentPartner : Document
    {
        public Partner? Partner { get; set; }
        public int PartnerId { get; set; }
        public int JobId { get; set; }

        public DocumentPartner(){}
    }
}
