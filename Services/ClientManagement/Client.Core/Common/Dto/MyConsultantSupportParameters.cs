namespace Client.Core.Common.Dto
{
    public  class MyConsultantSupportParameters : QueryStringParameters
    {
        public MyConsultantSupportParameters()
        {
            OrderBy = "ClientLastName";
        }
    }
}
