namespace ClientManagement.Core.Common.Dto
{
    public  class MySupportParameters : QueryStringParameters
    {
        public MySupportParameters()
        {
            OrderBy = "ClientLastName";
        }
    }
}
