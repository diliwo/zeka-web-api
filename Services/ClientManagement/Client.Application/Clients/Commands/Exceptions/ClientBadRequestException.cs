using ClientManagement.Application.Common.Exceptions;

namespace ClientManagement.Application.Clients.Commands.Exceptions
{
    public class ClientBadRequestException : BadRequestException
    {
        public ClientBadRequestException() : base("Parameter is null")
        {
        }
    }
}
