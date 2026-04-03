using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Rite.LeaveManagement.Svc.Models;
using Rite.LeaveManagement.Svc.Services;
using Microsoft.Extensions.Logging;
using Rite.LeaveManagement.Svc.Extensions;

namespace Rite.LeaveManagement.Svc.Controllers
{
    [ApiController]
    [Route("notifications")]
    public class NotificationController : ControllerBase
    {
        private readonly IMongoCollection<NotificationDocument> _notificationCollection;
        private readonly IMongoCollection<Employee> _employeeCollection;
        private readonly NotificationService _notificationService;
        private readonly IFcmNotificationService _fcmService;
        private readonly ILogger<NotificationController> _logger;

        public NotificationController(
            IOptions<Config.MongoDbSettings> mongoSettings,
            IFcmNotificationService fcmService,
            NotificationService notificationService,
            ILogger<NotificationController> logger)
        {
            _logger = logger;

            var client = new MongoClient(mongoSettings.Value.ConnectionString);
            var database = client.GetDatabase(mongoSettings.Value.DatabaseName);
            _fcmService = fcmService;
            _notificationCollection = database.GetCollection<NotificationDocument>("notifications");
            _employeeCollection = database.GetCollection<Employee>("employees");
            _notificationService = notificationService;

            _logger.LogDebug("NotificationController initialized with DB {DatabaseName}", mongoSettings.Value.DatabaseName);
        }

        [HttpPost("add")]
        public async Task<IActionResult> AddAnnouncement([FromBody] AnnouncementRequest request)
        {
            // Get current employee from HttpContext (set by middleware)
            var currentEmployee = HttpContext.GetCurrentEmployee();
            if (currentEmployee == null)
            {
                _logger.LogWarning("AddAnnouncement: Authentication required");
                return Unauthorized(new { error = "Authentication required" });
            }

            _logger.LogInformation("{Action} called by UserId: {UserId} with payload: {@Payload}",
                nameof(AddAnnouncement), currentEmployee.Id, request);

            try
            {
                ObjectId parsedUserId;

                if (request.Type.ToLower() != "notify")
                {
                    _logger.LogDebug("AddAnnouncement: calling _notificationService.AddNotificationAsync for Type={Type}", request.Type);
                    await _notificationService.AddNotificationAsync(request);
                }

                if (!ObjectId.TryParse(request.UserId, out parsedUserId))
                {
                    _logger.LogWarning(
                        "AddAnnouncement: invalid UserId '{UserId}', attempting to parse SentBy '{SentBy}' instead",
                        request.UserId,
                        request.SentBy);

                    if (!ObjectId.TryParse(request.SentBy, out parsedUserId))
                    {
                        _logger.LogWarning(
                            "AddAnnouncement: both UserId '{UserId}' and SentBy '{SentBy}' are invalid ObjectIds",
                            request.UserId,
                            request.SentBy);
                        return BadRequest(new { error = "Invalid userId" });
                    }
                }

                if (request.Type.ToLower() == "announcement")
                {
                    _logger.LogInformation("AddAnnouncement: broadcasting announcement to all employees");

                    var fcmTokens = await _employeeCollection
                        .Find(FilterDefinition<Employee>.Empty)
                        .Project(e => e.FcmToken)
                        .ToListAsync();

                    fcmTokens = fcmTokens
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Distinct()
                        .ToList();

                    _logger.LogInformation("AddAnnouncement: total FCM tokens for global announcement = {Count}", fcmTokens.Count);

                    if (fcmTokens.Any())
                    {
                        await _fcmService.SendPushNotificationsAsync(
                            fcmTokens,
                            $"{request.Title}",
                            $"{request.Body}",
                            new Dictionary<string, string>
                            {
                                ["screen"] = "Home",
                                ["leaveId"] = string.Empty
                            }
                        );
                    }
                    else
                    {
                        _logger.LogWarning("AddAnnouncement: no FCM tokens found for global announcement");
                    }
                }
                else
                {
                    _logger.LogInformation(
                        "AddAnnouncement: sending notification to specific user(s) based on parsedUserId={ParsedUserId}",
                        parsedUserId);

                    var fcmTokens = await GetTargetFcmTokens(parsedUserId);

                    _logger.LogInformation("AddAnnouncement: target-specific FCM token count = {Count}", fcmTokens.Count);

                    if (fcmTokens.Any())
                    {
                        await _fcmService.SendPushNotificationsAsync(
                            fcmTokens,
                            $"{request.Title}",
                            $"{request.Body}",
                            new Dictionary<string, string>
                            {
                                ["screen"] = "Home",
                                ["leaveId"] = string.Empty
                            }
                        );
                    }
                    else
                    {
                        _logger.LogWarning("AddAnnouncement: no FCM tokens resolved for parsedUserId={ParsedUserId}", parsedUserId);
                    }
                }

                _logger.LogInformation("AddAnnouncement: stored/sent successfully for Type={Type}", request.Type);

                return Ok(new { message = "Stored successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "AddAnnouncement: unhandled exception for Type={Type}, UserId={UserId}, SentBy={SentBy}",
                    request?.Type,
                    request?.UserId,
                    request?.SentBy);
                throw;
            }
        }

        [HttpPost("sendall")]
        public async Task<IActionResult> SendAnnouncements([FromBody] List<GenralAnnouncement> requests)
        {
            // Get current employee from HttpContext (set by middleware)
            var currentEmployee = HttpContext.GetCurrentEmployee();
            if (currentEmployee == null)
            {
                _logger.LogWarning("SendAnnouncements: Authentication required");
                return Unauthorized(new { error = "Authentication required" });
            }

            _logger.LogInformation("{Action} called by UserId: {UserId} with {Count} requests",
                nameof(SendAnnouncements), currentEmployee.Id, requests?.Count ?? 0);

            if (requests == null || requests.Count == 0)
            {
                _logger.LogWarning("SendAnnouncements: empty or null request list");
                return BadRequest(new { error = "At least one announcement is required." });
            }

            var tasks = requests.Select(async (req, idx) =>
            {
                try
                {
                    if (req == null)
                    {
                        _logger.LogWarning("SendAnnouncements: item at index {Index} is null", idx);
                        return (idx, ok: false, error: "Item is null.");
                    }

                    _logger.LogDebug(
                        "SendAnnouncements: processing item index {Index}, Title={Title}",
                        idx,
                        req.Title);

                    var tokens = new List<string>();
                    if (!string.IsNullOrWhiteSpace(req.FcmToken))
                        tokens.Add(req.FcmToken);

                    if (string.IsNullOrWhiteSpace(req.FcmToken))
                    {
                        _logger.LogWarning("SendAnnouncements: missing FCM token at index {Index}", idx);
                        return (idx, ok: false, error: "FCM token is required in request.");
                    }

                    tokens = tokens
                        .Where(t => !string.IsNullOrWhiteSpace(t))
                        .Distinct()
                        .ToList();

                    if (tokens.Count == 0)
                    {
                        _logger.LogWarning("SendAnnouncements: empty/distinct token list at index {Index}", idx);
                        return (idx, ok: false, error: "FCM token(s) required in request.");
                    }

                    _logger.LogInformation(
                        "SendAnnouncements: sending notification at index {Index} to {TokenCount} token(s)",
                        idx,
                        tokens.Count);

                    await _fcmService.SendPushNotificationsAsync(
                        tokens,
                        req.Title ?? string.Empty,
                        req.Body ?? string.Empty,
                        new Dictionary<string, string>
                        {
                            ["screen"] = "Home",
                            ["leaveId"] = string.Empty
                        }
                    );

                    return (idx, ok: true, error: (string)null);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex,
                        "SendAnnouncements: failed processing announcement at index {Index}",
                        idx);
                    return (idx, ok: false, error: "Internal error.");
                }
            });

            var results = await Task.WhenAll(tasks);

            var failures = results
                .Where(r => !r.ok)
                .Select(r => new { index = r.idx, error = r.error })
                .ToList();

            if (failures.Any())
            {
                _logger.LogWarning(
                    "SendAnnouncements: processed with partial failures. FailureCount={FailureCount}",
                    failures.Count);
                return StatusCode(207, new { message = "Processed with partial failures.", failures });
            }

            _logger.LogInformation("SendAnnouncements: all announcements processed successfully");

            return Ok(new { message = "All announcements processed successfully." });
        }

        [HttpGet("get")]
        public async Task<IActionResult> GetAnnouncement([FromQuery] DateTime? startDateUtc, [FromQuery] int days = 4)
        {
            // Get current employee from HttpContext (set by middleware)
            var currentEmployee = HttpContext.GetCurrentEmployee();
            if (currentEmployee == null)
            {
                _logger.LogWarning("GetAnnouncement: Authentication required");
                return Unauthorized(new { error = "Authentication required" });
            }

            _logger.LogInformation("{Action} called by UserId: {UserId}. startDateUtc={StartDateUtc}, days={Days}",
                nameof(GetAnnouncement), currentEmployee.Id, startDateUtc, days);

            if (days < 1) days = 1;

            DateTime start, end;

            if (startDateUtc.HasValue)
            {
                start = startDateUtc.Value.Date;
                end = start.AddDays(days);
            }
            else
            {
                end = DateTime.UtcNow.Date.AddDays(1);
                start = end.AddDays(-days);
            }

            _logger.LogDebug("GetAnnouncement: effective window Start={Start}, End={End}", start, end);

            var filter = Builders<NotificationDocument>.Filter.And(
                Builders<NotificationDocument>.Filter.Eq(n => n.Type, "ANNOUNCEMENT"),
                Builders<NotificationDocument>.Filter.Gte(n => n.SentAt, start),
                Builders<NotificationDocument>.Filter.Lt(n => n.SentAt, end)
            );

            var announcements = await _notificationCollection.Find(filter)
                .SortByDescending(n => n.SentAt)
                .ToListAsync();

            _logger.LogInformation("GetAnnouncement: found {Count} announcements in window", announcements.Count);

            if (!announcements.Any()) return Ok(Array.Empty<object>());

            var senderIds = announcements
                .Select(a => ObjectId.TryParse(a.SentBy, out var id) ? id : (ObjectId?)null)
                .Where(id => id.HasValue)
                .Select(id => id!.Value)
                .Distinct()
                .ToList();

            _logger.LogDebug("GetAnnouncement: distinct sender count={Count}", senderIds.Count);

            var employees = await _employeeCollection
                .Find(Builders<Employee>.Filter.In(e => e.Id, senderIds))
                .ToListAsync();

            var empDict = employees.ToDictionary(e => e.Id, e => e);

            var today = DateTime.UtcNow.Date;
            var result = announcements.Select(a =>
            {
                ObjectId.TryParse(a.SentBy, out var sid);
                empDict.TryGetValue(sid, out var emp);

                var daysAgo = (today - a.SentAt.Date).Days;
                var timeAgo = daysAgo switch
                {
                    0 => "Sent today",
                    1 => "Sent 1 day ago",
                    _ => $"Sent {daysAgo} days ago"
                };

                return new
                {
                    ProfileImage = emp?.ProfileImagePath,
                    SenderName = emp != null ? $"{emp.FirstName} {emp.LastName}".Trim() : null,
                    Title = a.Title,
                    Body = a.Body,
                    TimeAgo = timeAgo
                };
            });

            _logger.LogInformation("GetAnnouncement: returning {Count} announcement DTOs", announcements.Count);

            return Ok(result);
        }

        private async Task<List<string>> GetTargetFcmTokens(ObjectId applicantUserId, bool excludeSelf = true)
        {
            _logger.LogDebug("GetTargetFcmTokens called for ApplicantUserId={ApplicantUserId}, ExcludeSelf={ExcludeSelf}",
                applicantUserId,
                excludeSelf);

            var applicant = await _employeeCollection.Find(e => e.Id == applicantUserId).FirstOrDefaultAsync();
            if (applicant == null)
            {
                _logger.LogWarning("GetTargetFcmTokens: applicant not found for Id={ApplicantUserId}", applicantUserId);
                return new List<string>();
            }

            var reportingToIds = applicant.ReportingTo ?? new List<ObjectId>();
            var reportingToFilter = Builders<Employee>.Filter.In("_id", reportingToIds);

            var allowedRoles = new[] { "ADMIN", "HR", "SUPERADMIN" };
            var roleFilter = Builders<Employee>.Filter.In("role", allowedRoles);

            var combinedFilter = Builders<Employee>.Filter.Or(reportingToFilter, roleFilter);

            var targetEmployees = await _employeeCollection.Find(combinedFilter).ToListAsync();

            _logger.LogDebug(
                "GetTargetFcmTokens: found {TargetCount} target employees before filtering tokens, ApplicantUserId={ApplicantUserId}",
                targetEmployees.Count,
                applicantUserId);

            var tokens = targetEmployees
                .Where(e => !excludeSelf || e.Id != applicantUserId)
                .Select(e => e.FcmToken)
                .Where(token => !string.IsNullOrEmpty(token))
                .Distinct()
                .ToList();

            _logger.LogInformation(
                "GetTargetFcmTokens: returning {TokenCount} distinct FCM tokens for ApplicantUserId={ApplicantUserId}",
                tokens.Count,
                applicantUserId);

            return tokens;
        }
    }
}