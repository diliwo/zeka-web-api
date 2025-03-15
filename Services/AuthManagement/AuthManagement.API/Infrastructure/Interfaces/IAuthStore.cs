using AuthManagement.Models;

namespace AuthManagement.Infrastructure.Interfaces;

public interface IAuthStore
{
    Task<User?> VerifyUserLogin(string username, string password);
}