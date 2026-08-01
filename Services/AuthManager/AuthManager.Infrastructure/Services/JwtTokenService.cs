using AuthManager.Application.Common.Interfaces;
using AuthManager.Core.Models.Tokens;
using AuthManager.Core.Models.Users;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Zeka.Extensions.Authentication;

namespace AuthManager.Infrastructure.Services;

/// <summary>
/// Aimed to generate JWT
/// </summary>
public class JwtTokenService(AuthOptions options) : IJwtService
{
    private readonly string _issuer = options.AuthBaseAddress;

    public async Task<TokenResponse?> GenerateAuthenticationToken(User user, IEnumerable<string> roles)
    {
        var secretKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AuthenticationExtensions.SecurityKey));
        var signingCredentials = new SigningCredentials(secretKey, SecurityAlgorithms.HmacSha256);
        var expirationTimeStamp = DateTime.Now.AddMinutes(15);

        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id),
            new(JwtRegisteredClaimNames.Email, user.Email!)
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var tokenOptions = new JwtSecurityToken(
            issuer: _issuer,
            claims: claims,
            expires: expirationTimeStamp,
            signingCredentials: signingCredentials
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(tokenOptions);

        return new TokenResponse(tokenString, expirationTimeStamp.Subtract(DateTime.Now).TotalSeconds.ToString());
    }

    public string GenerateRefreshToken()
    {
        throw new NotImplementedException();
    }

    public string GenerateToken(User user, IEnumerable<string> roles)
    {
        throw new NotImplementedException();
    }
}
