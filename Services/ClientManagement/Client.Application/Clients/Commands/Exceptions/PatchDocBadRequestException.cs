using Client.Application.Common.Exceptions;

namespace Client.Application.Clients.Commands.Exceptions
{
    public class PatchDocBadRequestException : BadRequestException
    {
        public PatchDocBadRequestException() : base("patchDoc object sent from client is null.")
        {
        }
    }
}
