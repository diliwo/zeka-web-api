using Client.Core.Common;

namespace Client.Core.ValueObjects
{
    public class Language : ValueObject
    {
        public string SpokenLanguage { get; set; }


        public Language(string spokenLanguage)
        {
            SpokenLanguage = spokenLanguage;
        }

        protected override IEnumerable<object> GetEqualityComponents()
        {
            yield return SpokenLanguage;
        }
    }
}
