using DiliBeneficiary.Core.Common;

namespace DiliBeneficiary.Core.ValueObjects
{
    public class Reward : ValueObject
    {
        public string Type { get; set; }
        public Reward(){ }
        public Reward(string type) => Type = type;
        protected override IEnumerable<object> GetEqualityComponents()
        {
            throw new System.NotImplementedException();
        }

    }
}