using AdminAreaManagement.Core.Entities;
using System.Text.Json.Serialization;

namespace AdminAreaManagement.Application.DocumentPartners;

public sealed class DocumentFileOperationResult
{
    private readonly bool delete;

    public DocumentFileOperationResult(DocumentPartner document, bool deleteOperation)
    {
        Document = document;
        delete = deleteOperation;
    }

    [JsonIgnore]
    internal DocumentPartner Document { get; }

    public int DocumentId => Document.Id;
    public Guid OperationId => delete ? Document.DeleteOperationId!.Value : Document.CreateOperationId;
    public bool DatabaseCommitted => true;
    public DocumentFileOperationState State => delete
        ? Document.FileDeleteState ?? DocumentFileOperationState.Pending
        : Document.FileWriteState;
    public string? FailureCode => delete ? Document.FileDeleteFailureCode : Document.FileWriteFailureCode;
}
