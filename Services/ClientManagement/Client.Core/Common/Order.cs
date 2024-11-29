namespace ClientManagement.Core.Common
{
    public class Order
    {
        public int Column { get; set; }
        public string Direction { get; set; }

        public Order()
        {
            Column = 0;
            Direction = "asc";
        }

    }
}
