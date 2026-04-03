using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rite.LeaveManagement.Svc.Infrastructure.Logging
{
    public class RequestResponseLoggingMiddleware
    {
        private readonly RequestDelegate _next;
        private readonly ILogger<RequestResponseLoggingMiddleware> _logger;
        private const int MaxLen = 4000;

        public RequestResponseLoggingMiddleware(RequestDelegate next, ILogger<RequestResponseLoggingMiddleware> logger)
        {
            _next = next;
            _logger = logger;
        }

        public async Task Invoke(HttpContext context)
        {
            var corr = context.TraceIdentifier;
            using (_logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = corr }))
            {
                // Read request
                string requestBody = string.Empty;
                if (context.Request.ContentLength > 0 && IsTextual(context.Request.ContentType))
                {
                    context.Request.EnableBuffering();
                    using var reader = new StreamReader(context.Request.Body, Encoding.UTF8, leaveOpen: true);
                    requestBody = await reader.ReadToEndAsync();
                    context.Request.Body.Position = 0;
                }

                requestBody = MaskSensitive(requestBody);
                requestBody = Truncate(requestBody, MaxLen);

                _logger.LogInformation("HTTP {Method} {Path} Query={Query} Body={Body}",
                    context.Request.Method,
                    context.Request.Path,
                    context.Request.QueryString.ToString(),
                    requestBody);

                // Hook response
                var originalBody = context.Response.Body;
                using var mem = new MemoryStream();
                context.Response.Body = mem;

                await _next(context);

                mem.Position = 0;
                string responseText = string.Empty;
                if (IsTextual(context.Response.ContentType))
                {
                    using var sr = new StreamReader(mem, Encoding.UTF8, leaveOpen: true);
                    responseText = await sr.ReadToEndAsync();
                    mem.Position = 0;
                }

                responseText = MaskSensitive(responseText);
                responseText = Truncate(responseText, MaxLen);

                _logger.LogInformation("HTTP {StatusCode} for {Path} Body={Body}",
                    context.Response.StatusCode,
                    context.Request.Path,
                    responseText);

                await mem.CopyToAsync(originalBody);
                context.Response.Body = originalBody;
            }
        }

        private static bool IsTextual(string? contentType)
        {
            if (string.IsNullOrEmpty(contentType)) return false;
            return contentType.Contains("application/json") ||
                   contentType.Contains("text/") ||
                   contentType.Contains("application/xml") ||
                   contentType.Contains("application/problem+json");
        }

        private static string Truncate(string input, int max) =>
            string.IsNullOrEmpty(input) ? input : (input.Length <= max ? input : input.Substring(0, max) + "...[truncated]");

        private static string MaskSensitive(string input)
        {
            if (string.IsNullOrEmpty(input)) return input;
            string[] keys = new[] { "password", "pwd", "secret", "token", "jwt", "authorization" };
            foreach (var key in keys)
            {
                // naive JSON masking: "key":"value" -> "key":"***"
                var rx = new Regex($"(\"{key}\"\\s*:\\s*\")([^\"]+)(\")", RegexOptions.IgnoreCase);
                input = rx.Replace(input, $"$1***$3");
            }
            return input;
        }
    }
}
