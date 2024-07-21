namespace DiliBeneficiary.Application.Configuration
{
    public class FluidServiceConfiguration
    {
        public string BilanTemplateFilePath { get; set; }
        public string LogoCPASFilePath { get; set; }
        public string CpasNameFr { get; set; }
        public string CpasNameNl { get; set; }
        public string CpasZip { get; set; }
        public string CpasAdresse { get; set; }
        public string CallCenter { get; set; }
        public string EmailFr { get; set; }
        public string EmailNl { get; set; }

        public bool IsValid()
        {
            return !string.IsNullOrWhiteSpace(BilanTemplateFilePath) && File.Exists(BilanTemplateFilePath) &&
                   !string.IsNullOrWhiteSpace(LogoCPASFilePath) && File.Exists(LogoCPASFilePath);
        }
    }
}
