namespace AdminAreaManagement.Core.Common
{
    public abstract class Document : TenantOwnedEntity
    {
        public string Name { get; set; }
        public string ContentType { get; set; }
        public byte[] ContentFile { get; set; }
        public string Description { get; set; }
    }
}
