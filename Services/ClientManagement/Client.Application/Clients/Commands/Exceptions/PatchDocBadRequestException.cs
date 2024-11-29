using ClientManagement.Application.Common.Exceptions;

namespace ClientManagement.Application.Clients.Commands.Exceptions
{
    public class PatchDocBadRequestException : BadRequestException
    {
        public PatchDocBadRequestException() : base("patchDoc object sent from client is null.")
        {
        }
    }
}
