namespace ClientManagement.Infrastructure.Persistence.Helpers
{
    public static class ToStringUtcExtension {
        public static string ToStringUtc(this DateTime time)
        {
            return $"DateTime({time.Ticks}, DateTimeKind.Utc)";
        }
    }
}
