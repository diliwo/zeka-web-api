namespace AuthManager.Core.Models.Users;

public record LoginUserRequest(
	string UserNameOrEmail,
	string Password);
