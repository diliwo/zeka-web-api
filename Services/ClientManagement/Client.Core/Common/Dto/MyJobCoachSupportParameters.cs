namespace ClientManagement.Core.Common.Dto
{
    public  class MyJobCoachSupportParameters : QueryStringParameters
    {
        public MyJobCoachSupportParameters()
        {
            OrderBy = "ClientLastName";
        }
    }
}
