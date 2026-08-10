using AuthManager.Core.Models.Tokens;
using AuthManager.Core.Models.Users;

namespace AuthManager.Application.Common.Interfaces;

public interface IJwtService
{
	string GenerateToken(User user, IEnumerable<string> roles);

	string GenerateRefreshToken();
    Task<TokenResponse?> GenerateAuthenticationToken(User user, IEnumerable<string> roles);
}
