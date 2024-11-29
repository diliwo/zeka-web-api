namespace AdminAreaManagement.Application.Common.Exceptions;

public abstract class BadRequestException : Exception
{
    protected BadRequestException(string message) : base(message)
    {

    }
}