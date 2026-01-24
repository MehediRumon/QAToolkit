namespace QAToolkit.Helpers
{
    public static class DateTimeHelper
    {
        // Bangladesh Standard Time (UTC+6)
        public static DateTime BdNow
        {
            get
            {
                var bdTimeZone = TimeZoneInfo.FindSystemTimeZoneById("Bangladesh Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, bdTimeZone);
            }
        }
    }
}
