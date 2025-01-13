namespace ClientManagement.Core.Exceptions
{
    public class ClientAddressNotFoundException : Exception
    {
        public ClientAddressNotFoundException(string typeInfo, string data)
            :base($"The client address doesn't exist \"{typeInfo}\" \"{data}\"!")
        {
        }
    }
}
