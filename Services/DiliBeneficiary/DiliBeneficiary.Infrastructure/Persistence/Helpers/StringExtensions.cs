namespace DiliBeneficiary.Infrastructure.Persistence.Helpers
{
    public static class StringExtensions
    {
        public static bool Contains(this String str, String substr, StringComparison cmp)
        {
            if (substr == null)
                throw new ArgumentNullException("substring substring",
                    " cannot be null.");
  
            else if (!Enum.IsDefined(typeof(StringComparison), cmp))
                throw new ArgumentException("comp is not a member of",
                    "StringComparison, comp");

            return str.IndexOf(substr, cmp) >= 0;
        }
    }
}