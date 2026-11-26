using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AuthManager.Infrastructure.Onboarding;

internal sealed class RegistrationOptions : IDisposable
{
    private RegistrationOptions() { }

    public bool Enabled { get; private init; }
    public bool MailpitEnabled { get; private init; }
    public Uri? VerificationPage { get; private init; }
    public string Deployment { get; private init; } = string.Empty;
    public string ActiveKeyId { get; private init; } = string.Empty;
    public IReadOnlyDictionary<string, byte[]> Keys { get; private init; } = new Dictionary<string, byte[]>(StringComparer.Ordinal);

    public static RegistrationOptions Read(IConfiguration config, IHostEnvironment environment)
    {
        var section = config.GetSection("OnboardingRegistration");
        var enable = Flag(section["Enabled"]);
        var mailpit = Flag(section["VerificationMailpitEnabled"]);
        // Registration currently has one supported outbox delivery adapter.
        // Refuse startup rather than accept addresses whose mail cannot be sent.
        if (enable && !mailpit) throw InvalidConfiguration();
        if (mailpit && (!enable || !environment.IsEnvironment("LocalStack")))
            throw InvalidConfiguration();
        if (!enable) return new();

        var deployment = section["Deployment"];
        var active = section["ActiveKeyId"];
        if (!SafeIdentifier(deployment, 120) || !SafeIdentifier(active, 32))
            throw InvalidConfiguration();

        Uri? page = null;
        if (mailpit)
        {
            var origin = config["BrowserSession:Origin"];
            var path = section["VerificationPagePath"];
            if (!Uri.TryCreate(origin, UriKind.Absolute, out var browser)
                || browser.Scheme != Uri.UriSchemeHttps || browser.UserInfo.Length != 0
                || browser.GetLeftPart(UriPartial.Authority) != origin
                || !SafePagePath(path))
                throw InvalidConfiguration();
            page = new Uri(browser, path!);
        }

        var keys = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        try
        {
            foreach (var item in section.GetSection("VerificationKeyFiles").GetChildren())
            {
                var file = item.Value;
                if (!SafeIdentifier(item.Key, 32) || string.IsNullOrEmpty(file)
                    || !Path.IsPathFullyQualified(file) || keys.Count == 8)
                    throw InvalidConfiguration();

                var material = new byte[32];
                try
                {
                    using var stream = File.OpenRead(file);
                    if (stream.Length != material.Length) throw InvalidConfiguration();
                    stream.ReadExactly(material);
                    keys.Add(item.Key, material);
                }
                catch
                {
                    CryptographicOperations.ZeroMemory(material);
                    throw;
                }
            }
            if (!keys.ContainsKey(active!)) throw InvalidConfiguration();
            return new()
            {
                Enabled = true, MailpitEnabled = mailpit, VerificationPage = page,
                Deployment = deployment!, ActiveKeyId = active!, Keys = keys
            };
        }
        catch
        {
            foreach (var material in keys.Values) CryptographicOperations.ZeroMemory(material);
            throw InvalidConfiguration();
        }
    }

    internal static bool SafeIdentifier(string? input, int max)
        => input is { Length: > 0 } && input.Length <= max
            && input.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static bool SafePagePath(string? path)
        => path is { Length: > 1 and <= 128 } && path[0] == '/'
            && path[1..].Split('/').All(p => SafeIdentifier(p, 64));

    private static bool Flag(string? value) => value is null ? false
        : bool.TryParse(value, out var parsed) ? parsed : throw InvalidConfiguration();

    private static InvalidOperationException InvalidConfiguration()
        => new("ONBOARDING_REGISTRATION_CONFIGURATION_REJECTED");

    public void Dispose()
    {
        foreach (var material in Keys.Values) CryptographicOperations.ZeroMemory(material);
    }
}
