using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Zeka.Lifecycle.Contracts;

internal sealed class CanonicalLifecycleHash(string domain)
{
    private readonly StringBuilder _canonical = new(domain + "\n");

    public CanonicalLifecycleHash Add(string value)
    {
        var byteCount = Encoding.UTF8.GetByteCount(value);
        _canonical.Append(byteCount.ToString(CultureInfo.InvariantCulture))
            .Append(':').Append(value).Append('\n');
        return this;
    }

    public CanonicalLifecycleHash Add(Guid value) => Add(value.ToString("D"));
    public CanonicalLifecycleHash Add(long value) => Add(value.ToString(CultureInfo.InvariantCulture));
    public CanonicalLifecycleHash Add(int value) => Add(value.ToString(CultureInfo.InvariantCulture));
    public CanonicalLifecycleHash Add(DateTimeOffset value) => Add(value.ToString("O", CultureInfo.InvariantCulture));

    public string Finish() => Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(_canonical.ToString()))).ToLowerInvariant();

    public static CanonicalLifecycleHash ForHeader(string domain, LifecycleMessageHeaderV1 header) =>
        new CanonicalLifecycleHash(domain)
            .Add(header.OperationId)
            .Add(header.OrganisationId)
            .Add(header.OperationRevision)
            .Add(header.ParticipantId)
            .Add(header.ContractVersion)
            .Add(header.MessageId)
            .Add(header.CausationId)
            .Add(header.CorrelationId);
}
