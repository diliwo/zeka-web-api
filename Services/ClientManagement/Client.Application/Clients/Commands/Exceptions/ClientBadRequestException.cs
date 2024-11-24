using Client.Application.Common.Exceptions;

namespace Client.Application.Clients.Commands.Exceptions
{
    public class ClientBadRequestException : BadRequestException
    {
        public ClientBadRequestException() : base("Parameter is null")
        {
        }
    }
}
