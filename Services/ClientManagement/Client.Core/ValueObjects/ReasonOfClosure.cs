using ClientManagement.Core.Common;

namespace ClientManagement.Core.ValueObjects
{
    public class ReasonOfClosure : ValueObject
    {
        public string Reason { get; set; }
        public ReasonOfClosure(){ }
        public ReasonOfClosure(string reason) => Reason = reason;
        protected override IEnumerable<object> GetEqualityComponents()
        {
            throw new System.NotImplementedException();
        }

    }
}