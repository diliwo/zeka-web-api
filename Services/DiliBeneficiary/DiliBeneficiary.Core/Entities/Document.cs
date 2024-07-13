using DiliBeneficiary.Core.Common;

namespace DiliBeneficiary.Core.Entities
{
    public abstract class Document : Entity
    {
        public string Name { get; set; }
        public string ContentType { get; set; }
        public byte[] ContentFile { get; set; }
        public string Description { get; set; }
    }
}
