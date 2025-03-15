using AuthManagement.Models;

namespace AuthManagement.Infrastructure.Interfaces;

public interface ITokenService
{
    Task<AuthToken?> GenerateAuthenticationToken(string username, string password);
}