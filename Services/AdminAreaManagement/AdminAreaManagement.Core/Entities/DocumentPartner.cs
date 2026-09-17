using AdminAreaManagement.Core.Common;

namespace AdminAreaManagement.Core.Entities
{
    public enum DocumentFileOperationState
    {
        Pending = 0,
        Completed = 1,
        Failed = 2
    }

    public class DocumentPartner : Document
    {
        public Partner? Partner { get; set; }
        public int PartnerId { get; set; }
        public Guid CreateOperationId { get; private set; }
        public string CreateRequestHash { get; private set; } = string.Empty;
        public DocumentFileOperationState FileWriteState { get; private set; }
        public int FileWriteAttempts { get; private set; }
        public string? FileWriteFailureCode { get; private set; }
        public byte[]? PendingFileContent { get; private set; }
        public Guid? DeleteOperationId { get; private set; }
        public DocumentFileOperationState? FileDeleteState { get; private set; }
        public int FileDeleteAttempts { get; private set; }
        public string? FileDeleteFailureCode { get; private set; }

        public void BeginFileWrite(Guid operationId, string requestHash, byte[] content)
        {
            if (operationId == Guid.Empty || string.IsNullOrWhiteSpace(requestHash) || content.Length == 0)
                throw new InvalidOperationException("A valid document file operation is required.");
            CreateOperationId = operationId;
            CreateRequestHash = requestHash;
            PendingFileContent = content.ToArray();
            FileWriteState = DocumentFileOperationState.Pending;
        }

        public void CompleteFileWrite()
        {
            FileWriteAttempts++;
            FileWriteState = DocumentFileOperationState.Completed;
            FileWriteFailureCode = null;
            PendingFileContent = null;
        }

        public void FailFileWrite()
        {
            FileWriteAttempts++;
            FileWriteState = DocumentFileOperationState.Failed;
            FileWriteFailureCode = "document_storage_unavailable";
        }

        public void PendFileWrite()
        {
            FileWriteState = DocumentFileOperationState.Pending;
            FileWriteFailureCode = null;
        }

        public void BeginFileDelete(Guid operationId)
        {
            if (operationId == Guid.Empty) throw new InvalidOperationException("A valid document file operation is required.");
            if (DeleteOperationId is not null && DeleteOperationId != operationId)
                throw new InvalidOperationException("A different delete operation is already recorded for this document.");
            DeleteOperationId = operationId;
            FileDeleteState = DocumentFileOperationState.Pending;
            FileDeleteFailureCode = null;
        }

        public void CompleteFileDelete()
        {
            FileDeleteAttempts++;
            FileDeleteState = DocumentFileOperationState.Completed;
            FileDeleteFailureCode = null;
        }

        public void FailFileDelete()
        {
            FileDeleteAttempts++;
            FileDeleteState = DocumentFileOperationState.Failed;
            FileDeleteFailureCode = "document_storage_unavailable";
        }

        public void PendFileDelete()
        {
            FileDeleteState = DocumentFileOperationState.Pending;
            FileDeleteFailureCode = null;
        }

        public DocumentPartner() { }
    }
}
