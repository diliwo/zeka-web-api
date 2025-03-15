namespace AuthManagement.Models;
/// <summary>
/// Represent JWT
/// </summary>
/// <param name="Token"></param>
/// <param name="ExpiresIn"></param>
public record AuthToken(string Token, int ExpiresIn);