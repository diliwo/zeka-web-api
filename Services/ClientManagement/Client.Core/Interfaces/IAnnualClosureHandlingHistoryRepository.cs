namespace ClientManagement.Core.Interfaces
{
    public interface IAnnualClosureHandlingHistoryRepository
    {
        bool Exists(DateTime closureDate, string Niss);
        void AddHandling(DateTime closureDate, string Niss, string referntUserName);

    }
}
