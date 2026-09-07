using System.Globalization;
using ClientManagement.Core.Entities;
using ClientManagement.Core.Enums;
using ClientManagement.Core.ValueObjects;
using Language = ClientManagement.Core.ValueObjects.Language;

namespace ClientManagement.Tests.Common;

// Generated test data only; never copied from a person or an official example.
internal static class SyntheticClient
{
    public static string Niss(int year = 80, int month = 3, int day = 15,
        int sequence = 1, bool post2000 = false)
    {
        var body = year * 10_000_000L + month * 100_000L + day * 1_000L + sequence;
        var check = 97 - (body + (post2000 ? 2_000_000_000L : 0)) % 97;
        return body.ToString("D9", CultureInfo.InvariantCulture) + check.ToString("D2", CultureInfo.InvariantCulture);
    }

    public static Client Create(string? niss) => new(
        "synthetic-reference", CivilStatus.Divorced, "Synthetic", "Client", Gender.Male,
        new DateTime(1980, 3, 15), "Test place", "Test nationality", niss!,
        new Email("synthetic@example.invalid"), new Phone("0123456789"), new Phone("0123456789"),
        new Language("English"), new Language("Français"),
        new Address("1", "Test street", "1000", "Test city", "Test country"), "Synthetic worker");
}
