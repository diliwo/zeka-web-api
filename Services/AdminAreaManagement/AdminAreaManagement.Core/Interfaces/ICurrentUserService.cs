namespace AdminAreaManagement.Core.Interfaces
{
    public interface ICurrentUserService
    {
        string UserId { get; }
        string Username { get; }

    }
}
