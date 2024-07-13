namespace DiliBeneficiary.Core.Common.Dto
{
    public  class MyJobCoachSupportParameters : QueryStringParameters
    {
        public MyJobCoachSupportParameters()
        {
            OrderBy = "beneficiaryLastName";
        }
    }
}
