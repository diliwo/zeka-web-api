using ClientManagement.Core.Common;
using ClientManagement.Core.Exceptions;

namespace ClientManagement.Core.ValueObjects;

/// <summary>
/// Eleven canonical ASCII digits with an RN/BIS modulo-97 check.
/// Validates syntax and checksum, not registry assignment or personal attributes.
/// </summary>
public sealed class Niss : ValueObject
{
    public string Value { get; }

    private Niss(string value) => Value = value;

    public static Niss Parse(string? value)
    {
        if (value is null || value.Length != 11 || value.Any(c => c < '0' || c > '9'))
        {
            throw new InvalidNissFormatException();
        }

        long body = 0;
        for (var index = 0; index < 9; index++)
        {
            body = body * 10 + value[index] - '0';
        }

        var check = (value[9] - '0') * 10 + value[10] - '0';
        if (check != 97 - body % 97 && check != 97 - (2_000_000_000L + body) % 97)
        {
            throw new InvalidNissFormatException();
        }

        return new Niss(value);
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Value;
    }

    public override string ToString() => "[NISS redacted]";
}
