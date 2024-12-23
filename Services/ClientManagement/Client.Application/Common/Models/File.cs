namespace ClientManagement.Application.Common.Models
{
    public class FileDto
    {

        /// <summary>
        /// File name
        /// </summary>
        public string Name { get; set; }

        public byte[] Data { get; set; }
    }
}
