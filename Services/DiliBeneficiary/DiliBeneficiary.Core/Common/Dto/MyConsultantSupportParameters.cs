namespace DiliBeneficiary.Core.Common.Dto
{
    public  class MyConsultantSupportParameters : QueryStringParameters
    {
        public MyConsultantSupportParameters()
        {
            OrderBy = "beneficiaryLastName";
        }
    }
}
