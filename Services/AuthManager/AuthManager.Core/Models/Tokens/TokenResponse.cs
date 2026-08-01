namespace AuthManager.Core.Models.Tokens;
/// <summary>
/// Represent JWT
/// </summary>
/// <param name="Token"></param>
/// <param name="RefreshToken"></param>
public record TokenResponse(string Token, string RefreshToken);