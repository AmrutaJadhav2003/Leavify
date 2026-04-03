using Microsoft.AspNetCore.Http;
using MongoDB.Driver;
using Rite.LeaveManagement.Svc.Models;

namespace Rite.LeaveManagement.Svc.Middleware
{
    public class JwtDatabaseAuthMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<JwtDatabaseAuthMiddleware> _logger;

        public JwtDatabaseAuthMiddleware(RequestDelegate next, ILogger<JwtDatabaseAuthMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task InvokeAsync(HttpContext context, IMongoDatabase database)
        {
            var path = context.Request.Path.Value?.ToLower() ?? "";

            // Skip authentication for public endpoints
            if (path.Contains("/auth/login") ||
                path.Contains("/auth/versioninfo") ||
                path.Contains("/swagger") ||
                path.StartsWith("/leavedocs") ||
                path.StartsWith("/profilepics"))
            {
                await _next(context);
                return;
            }

            // Extract token from Authorization header
            if (!context.Request.Headers.TryGetValue("Authorization", out var authHeader))
            {
                _logger.LogWarning("Authorization header missing for path: {Path}", path);
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Authorization header missing" });
                return;
            }

            var token = authHeader.ToString();

            // Remove "Bearer " prefix if present
            if (token.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
            {
                token = token.Substring(7).Trim();
            }

            if (string.IsNullOrWhiteSpace(token))
            {
                _logger.LogWarning("Invalid token format for path: {Path}", path);
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Invalid token format" });
                return;
            }

            // Validate token exists in database
            var employeeCollection = database.GetCollection<Employee>("employees");
            var employee = await employeeCollection
                .Find(e => e.JwtToken == token)
                .FirstOrDefaultAsync();

            if (employee == null)
            {
                _logger.LogWarning("Invalid or expired JWT token for path: {Path}", path);
                context.Response.StatusCode = 401;
                await context.Response.WriteAsJsonAsync(new { error = "Invalid or expired JWT token." });
                return;
            }

            // Store employee and token in HttpContext for controller access
            context.Items["CurrentEmployee"] = employee;
            context.Items["JwtToken"] = token;
            context.Items["UserId"] = employee.Id.ToString();

            _logger.LogDebug("Authenticated user {UserId} for path {Path}", employee.Id, path);

            await _next(context);
        }
    }
}