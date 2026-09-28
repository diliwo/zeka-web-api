using ClientManagement.Application.Lifecycle;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClientManagement.Infrastructure.Persistence.Lifecycle;

public sealed class ClientExportFixtureScope(Guid organisationId) : IClientExportFixtureScope
{
    public Guid OrganisationId { get; } = organisationId != Guid.Empty
        ? organisationId
        : throw new ArgumentException("A fixture organisation is required.", nameof(organisationId));

    public void Demand(Guid requestedOrganisationId)
    {
        if (requestedOrganisationId != OrganisationId)
            throw new InvalidOperationException("client_export_nonfixture_scope_rejected");
    }
}

public static class ClientExportFixtureRegistration
{
    public static IServiceCollection AddClientExportFixtureParticipant(this IServiceCollection services,
        Guid fixtureOrganisationId, string artifactRoot)
    {
        ArgumentNullException.ThrowIfNull(services);
        var scope = new ClientExportFixtureScope(fixtureOrganisationId);
        services.AddSingleton<IClientExportFixtureScope>(scope);
        services.AddSingleton<IClientExportArtifactStore>(
            new DeterministicClientExportArtifactStore(artifactRoot, scope));
        services.TryAddSingleton(TimeProvider.System);
        services.AddScoped<IClientOrganisationExportParticipant, ClientOrganisationExportParticipant>();
        return services;
    }
}

public sealed class DeterministicClientExportArtifactStore : IClientExportArtifactStore
{
    private readonly string root;
    private readonly ClientExportFixtureScope scope;

    public DeterministicClientExportArtifactStore(string artifactRoot, ClientExportFixtureScope scope)
    {
        if (string.IsNullOrWhiteSpace(artifactRoot))
            throw new ArgumentException("A fixture artifact root is required.", nameof(artifactRoot));
        root = Path.GetFullPath(artifactRoot);
        this.scope = scope ?? throw new ArgumentNullException(nameof(scope));
        Directory.CreateDirectory(root);
        DemandOrdinaryDirectory(root);
    }

    public async Task<ClientExportArtifactReceipt> WriteVerifiedAsync(Guid organisationId, Guid operationId,
        ClientExportArtifactWrite artifact, CancellationToken cancellationToken)
    {
        scope.Demand(organisationId);
        DemandIdentity(operationId, artifact.SetName, artifact.RelativeName);
        var hash = ClientExportCanonical.Sha256(artifact.Content.Span);
        var directory = SetDirectory(organisationId, operationId, artifact.SetName, create: true);
        var fileName = $"{artifact.RelativeName}.{hash}.artifact";
        var destination = Contained(directory, fileName);
        var temporary = Contained(directory, $".{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(artifact.Content, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            try { File.Move(temporary, destination, overwrite: false); }
            catch (IOException) when (File.Exists(destination)) { }
            var bytes = await ReadOrdinaryFile(destination, cancellationToken);
            if (!bytes.AsSpan().SequenceEqual(artifact.Content.Span)
                || !StringComparer.Ordinal.Equals(ClientExportCanonical.Sha256(bytes), hash))
                throw new InvalidOperationException("client_export_artifact_identity_conflict");
            return new ClientExportArtifactReceipt(Reference(organisationId, operationId,
                artifact.SetName, fileName), hash, bytes.LongLength);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    public async Task VerifyExactAsync(Guid organisationId, Guid operationId, string setName,
        IReadOnlyCollection<ClientExportArtifactReceipt> expected, CancellationToken cancellationToken)
    {
        scope.Demand(organisationId);
        ArgumentNullException.ThrowIfNull(expected);
        DemandIdentity(operationId, setName, "fixture.csv");
        var directory = SetDirectory(organisationId, operationId, setName, create: false);
        var expectedByReference = expected.ToDictionary(x => x.ArtifactReference, StringComparer.Ordinal);
        if (expectedByReference.Count != expected.Count)
            throw new InvalidOperationException("client_export_artifact_receipt_duplicate");
        var actualFiles = Directory.EnumerateFileSystemEntries(directory).ToArray();
        foreach (var entry in actualFiles)
            if (!File.Exists(entry) || IsReparsePoint(entry))
                throw new InvalidOperationException("client_export_artifact_entry_invalid");
        var actualReferences = actualFiles.Select(path => Reference(organisationId, operationId,
            setName, Path.GetFileName(path))).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        if (!actualReferences.SequenceEqual(expectedByReference.Keys.OrderBy(x => x, StringComparer.Ordinal),
                StringComparer.Ordinal))
            throw new InvalidOperationException("client_export_artifact_set_mismatch");
        foreach (var reference in actualReferences)
        {
            var receipt = expectedByReference[reference];
            var path = Contained(directory, Path.GetFileName(reference));
            var bytes = await ReadOrdinaryFile(path, cancellationToken);
            if (bytes.LongLength != receipt.Length
                || !StringComparer.Ordinal.Equals(ClientExportCanonical.Sha256(bytes), receipt.ContentSha256))
                throw new InvalidOperationException("client_export_artifact_verification_failed");
        }
    }

    private string SetDirectory(Guid organisationId, Guid operationId, string setName, bool create)
    {
        var path = Contained(root, organisationId.ToString("N"), operationId.ToString("N"), setName);
        if (create) Directory.CreateDirectory(path);
        DemandOrdinaryDirectory(root);
        var current = root;
        foreach (var segment in new[] { organisationId.ToString("N"), operationId.ToString("N"), setName })
        {
            current = Contained(current, segment);
            DemandOrdinaryDirectory(current);
        }
        return path;
    }

    private static async Task<byte[]> ReadOrdinaryFile(string path, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || IsReparsePoint(path))
            throw new InvalidOperationException("client_export_artifact_file_invalid");
        return await File.ReadAllBytesAsync(path, cancellationToken);
    }

    private static void DemandIdentity(Guid operationId, string setName, string relativeName)
    {
        if (operationId == Guid.Empty) throw new ArgumentException("An operation is required.", nameof(operationId));
        DemandSegment(setName, nameof(setName));
        DemandSegment(relativeName, nameof(relativeName));
    }

    private static void DemandSegment(string value, string parameter)
    {
        if (string.IsNullOrWhiteSpace(value) || value is "." or ".."
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("Artifact path segment is invalid.", parameter);
    }

    private static string Contained(string parent, params string[] segments)
    {
        var path = Path.GetFullPath(segments.Aggregate(parent, Path.Combine));
        var prefix = Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar;
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            throw new InvalidOperationException("client_export_artifact_path_escape");
        return path;
    }

    private static void DemandOrdinaryDirectory(string path)
    {
        if (!Directory.Exists(path) || IsReparsePoint(path))
            throw new InvalidOperationException("client_export_artifact_directory_invalid");
    }

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static string Reference(Guid organisationId, Guid operationId, string setName, string fileName) =>
        $"client-fixture/{organisationId:N}/{operationId:N}/{setName}/{fileName}";
}
