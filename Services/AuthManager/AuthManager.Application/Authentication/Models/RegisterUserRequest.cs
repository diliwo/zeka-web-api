namespace AuthManager.Application.Authentication.Models;

public sealed record RegisterUserRequest(
    string Email,
    string UserName,
    string FirstName,
    string LastName,
    string Password);
