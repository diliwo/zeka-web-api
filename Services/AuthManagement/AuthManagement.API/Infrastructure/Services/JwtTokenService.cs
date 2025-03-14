﻿using AuthManagement.Infrastructure.Interfaces;
using AuthManagement.Models;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Zeka.Extensions.Authentication;

namespace AuthManagement.Infrastructure.Services;

/// <summary>
/// Aimed to generate JWT
/// </summary>
public class JwtTokenService : ITokenService
{
    private readonly IAuthStore _authStore;
    private readonly string _issuer;

    public JwtTokenService(IAuthStore authStore, AuthOptions options)
    {
        _authStore = authStore;
        _issuer = options.AuthBaseAddress;
    }

    public async Task<AuthToken?> GenerateAuthenticationToken(string username, string password)
    {
        var user = await _authStore.VerifyUserLogin(username, password);

        if (user is null)
        {
            return null;
        }

        var secretKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(AuthenticationExtensions.SecurityKey));
        var signingCredentials = new SigningCredentials(secretKey, SecurityAlgorithms.HmacSha256);
        var expirationTimeStamp = DateTime.Now.AddMinutes(15);

        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Name, user.Username),
            new Claim("user_role", user.Role)
        };

        var tokenOptions = new JwtSecurityToken(
            issuer: _issuer,
            claims: claims,
            expires: expirationTimeStamp,
            signingCredentials: signingCredentials
        );

        var tokenString = new JwtSecurityTokenHandler().WriteToken(tokenOptions);

        return new AuthToken(tokenString, (int)expirationTimeStamp.Subtract(DateTime.Now).TotalSeconds);
    }
}
