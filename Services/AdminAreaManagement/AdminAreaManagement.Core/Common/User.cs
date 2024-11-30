namespace AdminAreaManagement.Core.Common
{
    public class User
    {
        public int IdUser { get; set; }
        public string UserName { get; set; }
        public string Lastname { get; set; }
        public string Firstname { get; set; }
        public bool SoftDeleted { get; set; }
    }
}
