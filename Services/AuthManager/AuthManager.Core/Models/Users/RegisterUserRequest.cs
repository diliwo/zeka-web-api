namespace AuthManager.Core.Models.Users;

/// <summary>
/// Request model for registering a new user
/// </summary>
/// <param name="Email"></param>
/// <param name="UserName"></param>
/// <param name="FirstName"></param>
/// <param name="LastName"></param>
/// <param name="Password"></param>
public record RegisterUserRequest(
	string Email,
	string UserName,
	string FirstName,
	string LastName,
	string Password);
