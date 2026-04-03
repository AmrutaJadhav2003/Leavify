using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using Rite.LeaveManagement.Svc.Models;

namespace Rite.LeaveManagement.Svc.Extensions
{
    public static class HttpContextExtensions
    {
        public static Employee? GetCurrentEmployee(this HttpContext context)
        {
            return context.Items["CurrentEmployee"] as Employee;
        }

        public static string? GetJwtToken(this HttpContext context)
        {
            return context.Items["JwtToken"]?.ToString();
        }

        public static ObjectId GetCurrentUserId(this HttpContext context)
        {
            var employee = context.GetCurrentEmployee();
            return employee?.Id ?? ObjectId.Empty;
        }

        public static bool IsAuthenticated(this HttpContext context)
        {
            return context.Items["CurrentEmployee"] != null;
        }
    }
}