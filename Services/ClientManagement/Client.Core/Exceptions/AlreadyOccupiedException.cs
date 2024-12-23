namespace ClientManagement.Core.Exceptions
{
    public class AlreadyOccupiedException : Exception
    {
        public AlreadyOccupiedException(string jobTitle) 
            : base($"Le poste de \"{jobTitle}\" n'est plus vacant !")
        {
        }
    }
}
