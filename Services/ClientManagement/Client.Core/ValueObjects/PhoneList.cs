using System.Collections;
using Client.Core.Common;

namespace Client.Core.ValueObjects
{
    public class PhoneList : ValueObject, IEnumerable
    {
        private List<Phone> _phones { get; }

        public PhoneList(IEnumerable<Phone> phones)
        {
            _phones = phones.ToList();
        }

        public PhoneList(){}

        protected bool EqualsCore(PhoneList other)
        {
            return _phones.OrderBy(p => p.PhoneNumber).SequenceEqual(other._phones.OrderBy(p => p.PhoneNumber));
        }

        public IEnumerator<Phone> GerEnumerator()
        {
            return _phones.GetEnumerator();
        }

        IEnumerator IEnumerable.GetEnumerator()
        {
            return ((IEnumerable) this).GetEnumerator();
        }

        protected override IEnumerable<object> GetEqualityComponents()
        {
            throw new System.NotImplementedException();
        }
    }
}