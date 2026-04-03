using System;

namespace Rite.LeaveManagement.Svc.Models
{
    public static class DateTimeExtensions
    {
        private static readonly TimeZoneInfo IstTimeZone =
            TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");

        /// <summary>
        /// Converts a UTC DateTime to India Standard Time (IST).
        /// If the input is not UTC, it is first treated as UTC.
        /// </summary>
        public static DateTime ToIST(this DateTime utcDateTime)
        {
            if (utcDateTime.Kind != DateTimeKind.Utc)
            {
                utcDateTime = DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc);
            }
            return TimeZoneInfo.ConvertTimeFromUtc(utcDateTime, IstTimeZone);
        }
    }
}