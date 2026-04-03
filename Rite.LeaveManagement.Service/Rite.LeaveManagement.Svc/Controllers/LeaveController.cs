using Amazon.Runtime.Internal;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Rite.LeaveManagement.Svc.Models;
using Rite.LeaveManagement.Svc.Services;
using Rite.LeaveManagement.Svc.Extensions;
using System.Text;

namespace Rite.LeaveManagement.Svc.Controllers
{
    [ApiController]
    [Route("request")]
    public class LeaveController : ControllerBase
    {
        private readonly IMongoCollection<Leave> _leaveCollection;
        private readonly IMongoCollection<LeaveCategory> _leaveCategoryCollection;
        private readonly IMongoCollection<Employee> _employeeCollection;
        private readonly IMongoCollection<Roles> _rolesCollection;
        private readonly IMongoCollection<HolidayList> _holidayCollection;
        private readonly IFcmNotificationService _fcmService;
        private readonly IMongoCollection<NotificationDocument> _notificationCollection;
        private readonly NotificationService _notificationService;
        private readonly ILogger<LeaveController> _logger;

        public LeaveController(IOptions<Config.MongoDbSettings> mongoSettings, IFcmNotificationService fcmService, NotificationService notificationService, ILogger<LeaveController> logger)
        {
            var client = new MongoClient(mongoSettings.Value.ConnectionString);
            var database = client.GetDatabase(mongoSettings.Value.DatabaseName);
            _leaveCollection = database.GetCollection<Leave>("leaves");
            _employeeCollection = database.GetCollection<Employee>("employees");
            _fcmService = fcmService;
            _rolesCollection = database.GetCollection<Roles>("roles");
            _holidayCollection = database.GetCollection<HolidayList>("holidaylist");
            _notificationCollection = database.GetCollection<NotificationDocument>("notifications");
            _notificationService = notificationService;
            _leaveCategoryCollection = database.GetCollection<LeaveCategory>("leavecategories");
            _logger = logger;
        }

        [HttpPost("apply")]
        public async Task<IActionResult> ApplyLeave([FromBody] ApplyRequest request)
        {
            var currentEmployee = HttpContext.GetCurrentEmployee();
            if (currentEmployee == null)
            {
                return Unauthorized(new { error = "Authentication required" });
            }

            _logger.LogInformation("{Action} called by UserId: {UserId} with payload: {@Payload}",
                nameof(ApplyLeave), currentEmployee.Id, request);

            if (!ObjectId.TryParse(request.UserId, out var parsedUserId))
                return BadRequest(new { error = "Invalid userId" });

            var objectUserId = parsedUserId;
            var RequestedById = ObjectId.Parse(request.requestedBy);

            if (request.FromDate > request.ToDate)
                if (request.FromDate > request.ToDate)
                    return BadRequest(new { error = "FromDate cannot be after ToDate" });

            var type = request.Type.Trim().ToUpperInvariant();
            var applicant = await _employeeCollection.Find(e => e.Id == objectUserId).FirstOrDefaultAsync();
            var orgIdString = applicant.Organization.ToString();
            var holidaySet = await BuildHolidaySet(
            orgIdString, request.FromDate.Date, request.ToDate.Date, includeOptional: false);
            string requestType = string.Empty;
            //  Validate type-specific conditions
            switch (type)
            {
                case "LEAVE":
                    requestType = "LEAVE";
                    if (request.IsCompOff && (request.CompDates == null || !request.CompDates.Any()))
                        return BadRequest(new { error = "CompDates must be provided if isCompOff is true." });
                    break;

                //case "WFH":
                case "EXTRA":
                    requestType = "Extra working day(s)";
                    var dayRange = Enumerable.Range(0, (request.ToDate.Date - request.FromDate.Date).Days + 1)
        .Select(offset => request.FromDate.Date.AddDays(offset));

                    bool IsWeekend(DateTime d) =>
                        d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday;

                    bool IsHoliday(DateTime d) =>
                        holidaySet.Contains(d.Date);

                    // Allow EXTRA only on weekends OR public holidays
                    if (dayRange.Any(d => !IsWeekend(d) && !IsHoliday(d)))
                        return BadRequest(new
                        {
                            error = "EXTRA days are only allowed on weekends (Saturday/Sunday) or public holidays."
                        });
                    break;
                case "WFH":
                    requestType = "Work From Home";
                    break;

                case "HALFDAY":
                    if (!request.IsHalfDay)
                        return BadRequest(new
                        {
                            error = "HALFDAY requests must have isHalfDay = true."
                        });
                    break;

                default:
                    return BadRequest("Invalid request type.");
            }

            //  Prevent overlapping requests (any type, except REJECTED)
            //if (type.ToLower() != "extra")
            //{
            var overlapFilter = Builders<Leave>.Filter.And(
                    Builders<Leave>.Filter.Eq(l => l.UserId, objectUserId),
                    Builders<Leave>.Filter.Ne(l => l.Status, "DENIED"),
                    Builders<Leave>.Filter.Ne(l => l.Status, "CANCELLED"),
                    Builders<Leave>.Filter.Not(
                    Builders<Leave>.Filter.Or(
                        Builders<Leave>.Filter.Lt(l => l.ToDate, request.FromDate),
                        Builders<Leave>.Filter.Gt(l => l.FromDate, request.ToDate)
                        )
                    )
                );

            bool isConflict = await _leaveCollection.Find(overlapFilter).AnyAsync();
            if (isConflict)
                return Conflict(new { error = "You already have a  request that overlaps with this date range." });

            var currentEmployee_temp = await _employeeCollection.Find(e => e.Id == objectUserId).FirstOrDefaultAsync();
            if (currentEmployee_temp == null)
                return NotFound(new { error = "Employee not found." });

            if (type == "LEAVE")
            {
                double requestedDays = (request.ToDate - request.FromDate).Days + 1;

                if (request.IsHalfDay)
                    requestedDays = requestedDays / 2; // Treat half day as 1 unit or 0.5 if you support that

                //if (currentEmployee.Balance < requestedDays)
                //{
                //    return BadRequest(new
                //    {
                //        error = "Insufficient leave balance. Cannot apply for requested days."
                //    });
                //}
            }
            // Insert document
            var leaveId = ObjectId.GenerateNewId(); // generate leave ID manually
            var storedDocs = new List<StoredLeaveDocument>();

            if (request.Documents != null && request.Documents.Any())
            {
                var allowedTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    { "pdf", "pdf" }, { "jpg", "jpg" }, { "jpeg", "jpeg" },
                    { "png", "png" }, { "doc", "doc" }, { "docx", "docx" }
                };

                var baseStoragePath = Environment.GetEnvironmentVariable("STORAGE_ROOT_PATH")
                      ?? "/data/leavify";

                var storagePath = Path.Combine(baseStoragePath, "leavedocs", request.UserId, leaveId.ToString());
                Directory.CreateDirectory(storagePath);

                foreach (var doc in request.Documents)
                {
                    var fileExt = allowedTypes.ContainsKey(doc.DocType ?? "") ? allowedTypes[doc.DocType] : "pdf";
                    var fileName = $"{Guid.NewGuid():N}.{fileExt}";
                    var fullPath = Path.Combine(storagePath, fileName);

                    try
                    {
                        var fileBytes = Convert.FromBase64String(doc.DocBytes);
                        await System.IO.File.WriteAllBytesAsync(fullPath, fileBytes);

                        storedDocs.Add(new StoredLeaveDocument
                        {
                            DocType = fileExt,
                            DocPath = Path.Combine("leavedocs", request.UserId, leaveId.ToString(), fileName).Replace("\\", "/"),
                            DocBytes = doc.DocBytes
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unhandled exception in LeaveController.");
                        return StatusCode(500, new { error = "Failed to save document", detail = ex.Message });
                    }
                }
            }

            string reqStatus = "PENDING";
            var trackingList = new List<RequestStatusTracking>();
            if (RequestedById != objectUserId)
            {
                reqStatus = "APPROVED";
                var manager = await _employeeCollection.Find(e => e.Id == RequestedById).FirstOrDefaultAsync();
                if (manager == null)
                    return NotFound(new { error = "manager not found." });

                trackingList.Add(new RequestStatusTracking
                {
                    Status = reqStatus,
                    ProcessedBy = manager.FirstName + ' ' + manager.LastName,
                    ProcessedById = manager.Id,
                    ProcessedAt = DateTime.UtcNow.ToIST(),
                    Comment = $"Manager applied {requestType} on behalf of the employee"
                });
            }

            var newLeave = new Leave
            {
                Id = leaveId,
                RequestedBy = RequestedById,
                FirstName = currentEmployee_temp.FirstName,
                LastName = currentEmployee_temp.LastName,
                UserId = objectUserId,
                Type = type,
                SubType = request.SubType,
                FromDate = request.FromDate,
                ToDate = request.ToDate,
                Reason = request.Reason,
                IsCompOff = request.IsCompOff,
                IsHalfDay = request.IsHalfDay,
                CompDates = request.CompDates?.Select(DateTime.Parse).ToList() ?? new(),
                Documents = storedDocs,
                Status = reqStatus,
                IsEscalated = false,
                CreatedAt = DateTime.UtcNow.ToIST(),
                UpdatedAt = DateTime.UtcNow.ToIST(),
                ReqStatusTracking = trackingList,//new List<RequestStatusTracking>(),
                ReminderDetails = new ReminderDetails(),
                Category = request.Category == null ? "" : request.Category.Trim()
            };

            //await _leaveCollection.InsertOneAsync(newLeave);

            if (RequestedById != objectUserId)
            {
                decimal ComputeDaysToAdjust(Leave leave, HashSet<DateTime> holidaySet)
                {
                    var type = (leave.Type ?? "").Trim().ToUpperInvariant();
                    bool isExtra = false;

                    if (type.ToLower() == "extra")
                    {
                        isExtra = true;
                    }

                    // Always decimal literals
                    if (type == "WFH")
                        return 0m;

                    double workingDays = GetWorkingDaysInclusive(
                    leave.FromDate.Date,
                    leave.ToDate.Date,
                    holidaySet, isExtra
                );

                    if (leave.IsHalfDay)
                    {
                        workingDays = workingDays / 2;
                    }
                    if (workingDays <= 0 && !leave.IsHalfDay)
                        return 0m;

                    // Explicit conversion
                    return (decimal)workingDays;
                }

                Leave leave = new Leave();
                leave.Type = type;
                leave.FromDate = request.FromDate.Date;
                leave.ToDate = request.ToDate.Date;
                leave.FirstName = currentEmployee_temp.FirstName;
                leave.LastName = currentEmployee_temp.LastName;
                leave.IsHalfDay = request.IsHalfDay;
                var daysToDeduct = ComputeDaysToAdjust(leave, holidaySet);

                await _leaveCollection.InsertOneAsync(newLeave);

                if (type.ToLower() == "leave")
                {
                    await _employeeCollection.UpdateOneAsync(
                                   e => e.Id == objectUserId,
                                   Builders<Employee>.Update.Inc(e => e.Balance, -daysToDeduct)
                               );
                }
                else if (type.ToLower() == "extra")
                {
                    await _employeeCollection.UpdateOneAsync(
                                   e => e.Id == objectUserId,
                                   Builders<Employee>.Update.Inc(e => e.Balance, daysToDeduct)
                               );
                }
            }
            else
            {
                await _leaveCollection.InsertOneAsync(newLeave);
            }

            var fcmTokens = await GetTargetFcmMessages(objectUserId, objectUserId, leaveId.ToString());
            string typeLabel = request.Type.Trim().ToUpperInvariant() switch
            {
                "LEAVE" => "Leave",
                "WFH" => "Work From Home",
                "EXTRA" => "Extra Working Day",
                "HALFDAY" => "Half Day Leave",
                _ => "Leave"
            };

            if (fcmTokens.Any())
            {
                if (RequestedById != objectUserId)
                {
                    var manager = await _employeeCollection.Find(e => e.Id == RequestedById).FirstOrDefaultAsync();
                    applicant = await _employeeCollection.Find(e => e.Id == objectUserId).FirstOrDefaultAsync();
                    if (manager == null)
                        return NotFound(new { error = "manager not found." });

                    var result = await _fcmService.SendPushNotificationsAsync(
                               fcmTokens,
                               $"{typeLabel} Notification",
                               $"{manager.FirstName} {manager.LastName} has applied {typeLabel.ToLower()} on behalf of {applicant.FirstName} {applicant.LastName}."
                           );
                }
                else
                {
                    var result = await _fcmService.SendPushNotificationsAsync(
                   fcmTokens,
                   $"{typeLabel} Notification",
                   $"{currentEmployee_temp.FirstName} {currentEmployee_temp.LastName} has requested {typeLabel.ToLower()}");
                }
            }

            await _notificationService.AddNotificationAsync(new AnnouncementRequest
            {
                Title = $"{typeLabel} Notification",
                Body = $"{currentEmployee_temp.FirstName} {currentEmployee_temp.LastName} has request {typeLabel.ToLower()}",
                Screen = "Leave",
                Type = "NOTIFICATION",
                SentBy = currentEmployee_temp.Id.ToString(),
                UserId = currentEmployee_temp.Id.ToString(),
                LeaveId = leaveId.ToString()
            });
            // Send email notification
            await SendLeaveEmailAsync(newLeave.Id.ToString());

            return Ok(new { success = true, leaveId = newLeave.Id.ToString() });
        }

        [HttpPost("process")]
        public async Task<IActionResult> ProcessLeave([FromBody] ProcessRequest request)
        {
            var currentEmployee = HttpContext.GetCurrentEmployee();
            if (currentEmployee == null)
            {
                return Unauthorized(new { error = "Authentication required" });
            }

            _logger.LogInformation("{Action} called by UserId: {UserId} with payload: {@Payload}",
                nameof(ProcessLeave), currentEmployee.Id, request);

            if (!ObjectId.TryParse(request.LeaveId, out var leaveObjectId))
                return BadRequest(new { error = "Invalid leaveId" });

            if (!ObjectId.TryParse(request.ActionTakenBy, out var actionByObjectId))
                return BadRequest(new { error = "Invalid actionTakenBy" });

            // 2) Load leave
            var leave = await _leaveCollection
                .Find(l => l.Id == leaveObjectId)
                .FirstOrDefaultAsync();
            if (leave == null)
                return NotFound(new { error = "Application request not found" });

            var approvingMgr = currentEmployee;

            // 3) Prepare for this manager’s new entry
            var trackingList = leave.ReqStatusTracking ?? new List<RequestStatusTracking>();
            var normalizedStatus = request.Status.Trim().ToUpperInvariant();
            var processingEmp = await _employeeCollection.Find(e => e.Id == ObjectId.Parse(request.ActionTakenBy)).FirstOrDefaultAsync();
            var managerName = $"{processingEmp.FirstName} {processingEmp.LastName}".Trim();

            // 4) If their latest entry already equals this status → no-op
            var lastEntry = trackingList
                .Where(t => t.ProcessedBy == managerName)
                .OrderByDescending(t => t.ProcessedAt)
                .FirstOrDefault();
            //if (lastEntry != null && lastEntry.Status == normalizedStatus)
            //    return Ok(new { success = true, message = "No change; latest status already set." });

            // 5) Append a _new_ entry (do NOT remove old ones)

            trackingList.Add(new RequestStatusTracking
            {
                Status = normalizedStatus,
                ProcessedBy = managerName,
                ProcessedById = processingEmp.Id,
                ProcessedAt = DateTime.UtcNow.ToIST(),
                Comment = request.Comment
            });

            // 6) Fetch applicant & their reporting managers
            var applicant = await _employeeCollection.Find(e => e.Id == leave.UserId).FirstOrDefaultAsync();
            var reportingManagers = applicant?.ReportingTo ?? new List<ObjectId>();
            if (!reportingManagers.Any())
                return BadRequest(new { error = "No reporting managers assigned." });

            // 7) Get each manager’s latest status
            var mgrDocs = await _employeeCollection
                .Find(e => reportingManagers.Contains(e.Id))
                .ToListAsync();
            var managerNames = mgrDocs
                .Select(m => $"{m.FirstName} {m.LastName}".Trim())
                .ToList();

            var latestStatusMap = trackingList
                .Where(t => managerNames.Contains(t.ProcessedBy))
                .GroupBy(t => t.ProcessedBy)
                .ToDictionary(
                    g => g.Key,
                    g => g.OrderBy(x => x.ProcessedAt).Last().Status
                );

            // 8) Decide final leave status
            //var allApproved = managerNames.All(n => latestStatusMap.TryGetValue(n, out var s) && s == "APPROVED");
            //var allDenied = managerNames.All(n => latestStatusMap.TryGetValue(n, out var s) && s == "REJECTED");
            //var finalStatusToSet = allApproved ? "APPROVED"
            //                : allDenied ? "DENIED"
            //                : "PENDING";

            var anyRejected = managerNames.Any(n => latestStatusMap.TryGetValue(n, out var s) && s == "REJECTED");
            var allApproved = managerNames.All(n => latestStatusMap.TryGetValue(n, out var s) && s == "APPROVED");

            string finalStatusToSet;
            if (anyRejected)
                finalStatusToSet = "REJECTED";
            else if (allApproved)
                finalStatusToSet = "APPROVED";
            else
                finalStatusToSet = "PENDING";

            var oldStatus = leave.Status?.Trim().ToUpperInvariant();
            var newStatus = finalStatusToSet;

            bool goingToApproved = oldStatus != "APPROVED" && newStatus == "APPROVED";
            bool leavingApproved = oldStatus == "APPROVED" && newStatus != "APPROVED";

            var orgIdString = applicant.Organization.ToString();
            var holidaySet = await BuildHolidaySet(
                orgIdString, leave.FromDate.Date, leave.ToDate.Date, includeOptional: false);

            //double ComputeDaysToAdjust(Leave lv, HashSet<DateTime> holidaySetLocal)
            //{
            //    var type = (lv.Type ?? "").Trim().ToUpperInvariant();

            //    if (type == "WFH" || type == "EXTRA")
            //        return 0; // no balance change

            //    var workingDays = GetWorkingDaysInclusive(
            //        lv.FromDate.Date, lv.ToDate.Date, holidaySetLocal);

            //    if (type == "HALFDAY")
            //        return workingDays > 0 ? 0.5 : 0.0;

            //    // LEAVE or others -> full working days
            //    return workingDays;
            //}
            decimal ComputeDaysToAdjust(Leave leave, HashSet<DateTime> holidaySet)
            {
                var type = (leave.Type ?? "").Trim().ToUpperInvariant();

                bool isExtra = false;

                if (type.ToLower() == "extra")
                {
                    isExtra = true;
                }

                // Always decimal literals
                if (type == "WFH")
                    return 0m;

                double workingDays = GetWorkingDaysInclusive(
                    leave.FromDate.Date,
                    leave.ToDate.Date,
                    holidaySet,
                    isExtra
                );

                if (leave.IsHalfDay)
                {
                    workingDays = workingDays / 2;
                }
                if (workingDays <= 0 && !leave.IsHalfDay)
                    return 0m;

                //if (type == "HALFDAY")
                //    return 0.5m;

                // Explicit conversion
                return (decimal)workingDays;
            }

            decimal days = ComputeDaysToAdjust(leave, holidaySet);
            if (days > 0)
            {
                var type = (leave.Type ?? "").Trim().ToUpperInvariant();
                bool isExtra = type == "EXTRA";

                if (goingToApproved)
                {
                    // LEAVE: subtract days, EXTRA: add days
                    decimal adjustment = isExtra ? days : -days;
                    await _employeeCollection.UpdateOneAsync(
                        e => e.Id == leave.UserId,
                        Builders<Employee>.Update.Inc(e => e.Balance, adjustment)
                    );
                }
                else if (leavingApproved)
                {
                    // LEAVE: return days (+), EXTRA: remove days (-)
                    decimal adjustment = isExtra ? -days : days;
                    await _employeeCollection.UpdateOneAsync(
                        e => e.Id == leave.UserId,
                        Builders<Employee>.Update.Inc(e => e.Balance, adjustment)
                    );
                }
            }
            // 9) Persist leave + updated tracking
            var update = Builders<Leave>.Update
                .Set(l => l.Status, finalStatusToSet)
                .Set(l => l.UpdatedAt, DateTime.UtcNow.ToIST())
                .Set(l => l.ReqStatusTracking, trackingList);
            await _leaveCollection.UpdateOneAsync(l => l.Id == leave.Id, update);

            // Deduct balance if finally approved
            //if (goingToApproved)  // <-- from the new block
            //{
            //    double daysToDeduct = ComputeDaysToAdjust(leave);
            //    if (daysToDeduct > 0)
            //    {
            //        await _employeeCollection.UpdateOneAsync(
            //            e => e.Id == leave.UserId,
            //            Builders<Employee>.Update.Inc(e => e.Balance, -daysToDeduct)
            //        );
            //    }
            //}


            // Notification
            var fcmTokens = await GetTargetFcmMessages(leave.UserId, leave.UserId, leave.Id.ToString(), false);
            string typeLabel = leave.Type.Trim().ToUpperInvariant() switch
            {
                "LEAVE" => "Leave",
                "WFH" => "Work From Home",
                "EXTRA" => "Extra Working Day",
                "HALFDAY" => "Half Day Leave",
                _ => "Leave"
            };

            var requesterId = leave.UserId;
            var processorId = actionByObjectId;
            var employeeFilter = Builders<Employee>.Filter.In(e => e.Id, new[] { requesterId, processorId });
            var employeeList = await _employeeCollection.Find(employeeFilter).ToListAsync();

            var requester = employeeList.FirstOrDefault(e => e.Id == requesterId);
            var processor = employeeList.FirstOrDefault(e => e.Id == processorId);
            var requesterName = $"{requester?.FirstName} {requester?.LastName}".Trim();
            var processorName = $"{processor?.FirstName} {processor?.LastName}".Trim();

            if (fcmTokens.Any())
            {
                await _fcmService.SendPushNotificationsAsync(
                    fcmTokens,
                    $"{normalizedStatus.ToUpper()}",
                    $"{typeLabel.ToLower()} request from {requesterName} has been {normalizedStatus.ToLower()} by {processorName}."
                );
            }
            await _notificationService.AddNotificationAsync(new AnnouncementRequest
            {
                Title = $"{normalizedStatus.ToUpper()}",
                Body = $"{typeLabel.ToLower()} request from {requesterName} has been {normalizedStatus.ToLower()} by {processorName}.",
                Screen = "Leave",
                Type = "NOTIFICATION",
                SentBy = processor.Id.ToString(),
                UserId = requesterId.ToString(),
                LeaveId = leaveObjectId.ToString()
            });

            return Ok(new { success = true, message = "Application processed successfully." });
        }

        private async Task<HashSet<DateTime>> BuildHolidaySet(
    string organizationId, DateTime from, DateTime to, bool includeOptional = false)
        {
            // Collect spanned years
            var years = Enumerable.Range(from.Year, to.Year - from.Year + 1)
                                  .Select(y => y.ToString())
                                  .ToList();

            var filter = Builders<HolidayList>.Filter.And(
                Builders<HolidayList>.Filter.Eq(h => h.OrganizationId, organizationId),
                Builders<HolidayList>.Filter.In(h => h.Year, years)
            );

            var holidayDocs = await _holidayCollection.Find(filter).ToListAsync();

            var holidays = new HashSet<DateTime>();
            var exchangedWorking = new HashSet<DateTime>();

            foreach (var doc in holidayDocs)
            {
                foreach (var h in doc.HolidayDates ?? Enumerable.Empty<HolidayDate>())
                {
                    if (!includeOptional && h.IsOptional) continue;

                    if (DateTime.TryParse(h.Date, out var hd))
                    {
                        holidays.Add(hd.Date);

                        if (!string.IsNullOrWhiteSpace(h.ExchangedWith) &&
                            DateTime.TryParse(h.ExchangedWith, out var ex))
                        {
                            exchangedWorking.Add(ex.Date);
                        }
                    }
                }
            }

            // ExchangedWith dates become working days -> remove from holidays
            holidays.ExceptWith(exchangedWorking);

            // Keep only range
            holidays.RemoveWhere(d => d < from.Date || d > to.Date);

            return holidays;
        }

        private static int GetWorkingDaysInclusive(
            DateTime from, DateTime to, HashSet<DateTime> holidays, bool isExtra = false)
        {
            int count = 0;
            for (var d = from.Date; d <= to.Date; d = d.AddDays(1))
            {
                if (!isExtra)
                {
                    if (d.DayOfWeek == DayOfWeek.Saturday || d.DayOfWeek == DayOfWeek.Sunday) continue;
                    if (holidays.Contains(d)) continue;
                }
                count++;
            }
            return count;
        }

        [HttpPost("edit")]
        public async Task<IActionResult> EditLeave([FromBody] EditRequest request)
        {
            // ==== 1. Validate JWT ====
            var currentEmployee = HttpContext.GetCurrentEmployee();
            if (currentEmployee == null)
            {
                return Unauthorized(new { error = "Authentication required" });
            }

            _logger.LogInformation("{Action} called by UserId: {UserId} with payload: {@Payload}",
                nameof(EditLeave), currentEmployee.Id, request);
            _logger.LogInformation("{Action} called with payload: {@Payload}", nameof(EditLeave), request);
            _logger.LogInformation("{Action} called with payload: {@Payload}", nameof(EditLeave), request);

            // ==== 2. Validate inputs ====
            if (string.IsNullOrWhiteSpace(request.LeaveId) || !ObjectId.TryParse(request.LeaveId, out var leaveObjectId))
                return BadRequest(new { error = "Invalid LeaveId" });

            if (!ObjectId.TryParse(request.UserId, out var parsedUserId))
                return BadRequest(new { error = "Invalid userId" });

            var objectUserId = parsedUserId;

            var leave = await _leaveCollection.Find(l => l.Id == leaveObjectId && l.UserId == objectUserId).FirstOrDefaultAsync();
            if (leave == null)
                return NotFound(new { error = "Request not found" });

            if (request.FromDate.HasValue && request.ToDate.HasValue && request.FromDate > request.ToDate)
                return BadRequest(new { error = "FromDate cannot be after ToDate" });

            if (request.FromDate.HasValue && request.ToDate.HasValue)
            {
                var overlapFilter = Builders<Leave>.Filter.And(
                    Builders<Leave>.Filter.Eq(l => l.UserId, objectUserId),
                    Builders<Leave>.Filter.Ne(l => l.Status, "DENIED"),
                    Builders<Leave>.Filter.Ne(l => l.Id, leaveObjectId),
                    Builders<Leave>.Filter.Not(
                        Builders<Leave>.Filter.Or(
                            Builders<Leave>.Filter.Lt(l => l.ToDate, request.FromDate.Value),
                            Builders<Leave>.Filter.Gt(l => l.FromDate, request.ToDate.Value)
                        )
                    )
                );

                bool isConflict = await _leaveCollection.Find(overlapFilter).AnyAsync();
                if (isConflict)
                    return Conflict(new { error = "You already have a leave request that overlaps with this date range." });

                if (leave.Type == "EXTRA")
                {
                    var dayRange = Enumerable.Range(0, (request.ToDate.Value - request.FromDate.Value).Days + 1)
                                             .Select(offset => request.FromDate.Value.AddDays(offset));

                    if (dayRange.Any(d => d.DayOfWeek != DayOfWeek.Saturday && d.DayOfWeek != DayOfWeek.Sunday))
                        return BadRequest(new { error = "EXTRA days working are only allowed on weekends (Saturday/Sunday)." });
                }
            }

            // ==== 3. Save uploaded documents (if any) ====
            List<StoredLeaveDocument> storedDocs = new();
            if (request.Documents != null && request.Documents.Any())
            {
                var allowedTypes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            { "pdf", "pdf" }, { "jpg", "jpg" }, { "jpeg", "jpeg" },
            { "png", "png" }, { "doc", "doc" }, { "docx", "docx" }
        };

                var baseStoragePath = Environment.GetEnvironmentVariable("STORAGE_ROOT_PATH")
                      ?? "/data/leavify";
                var storagePath = Path.Combine(baseStoragePath, "leavedocs", request.UserId, request.LeaveId);
                Directory.CreateDirectory(storagePath);

                foreach (var doc in request.Documents)
                {
                    var fileExt = allowedTypes.ContainsKey(doc.DocType ?? "") ? allowedTypes[doc.DocType] : "pdf";
                    var fileName = $"{Guid.NewGuid():N}.{fileExt}";
                    var fullPath = Path.Combine(storagePath, fileName);

                    try
                    {
                        var fileBytes = Convert.FromBase64String(doc.DocBytes);
                        await System.IO.File.WriteAllBytesAsync(fullPath, fileBytes);

                        storedDocs.Add(new StoredLeaveDocument
                        {
                            DocType = fileExt,
                            DocPath = Path.Combine("leavedocs", request.UserId, request.LeaveId, fileName).Replace("\\", "/"),
                            DocBytes = doc.DocBytes
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Unhandled exception in LeaveController.");
                        return StatusCode(500, new { error = "Failed to save document", detail = ex.Message });
                    }
                }
            }

            var processingEmp = await _employeeCollection.Find(e => e.Id == objectUserId).FirstOrDefaultAsync();

            // ==== 4. Change detection helpers ====
            bool DateChanged(DateTime? req, DateTime existing) => req.HasValue && req.Value.Date != existing.Date;
            bool StringChanged(string? req, string? existing) =>
                !string.IsNullOrWhiteSpace(req) && !string.Equals(req.Trim(), (existing ?? "").Trim(), StringComparison.Ordinal);
            bool BoolChanged(bool? req, bool existing) => req.HasValue && req.Value != existing;
            bool ListDateChanged(IEnumerable<string>? req, IList<DateTime>? existing)
            {
                if (req == null) return false;
                var parsed = req.Select(DateTime.Parse).Select(d => d.Date).OrderBy(d => d).ToList();
                var curr = (existing ?? new List<DateTime>()).Select(d => d.Date).OrderBy(d => d).ToList();
                if (parsed.Count != curr.Count) return true;
                for (int i = 0; i < parsed.Count; i++) if (parsed[i] != curr[i]) return true;
                return false;
            }

            string Trunc(string? s, int n = 100) => string.IsNullOrWhiteSpace(s) ? "" : s.Trim().Length <= n ? s.Trim() : s.Trim()[..n] + "...";
            string D(DateTime dt) => dt.ToIST().ToString("yyyy-MM-dd");

            // ==== 5. Detect diffs and build update + change summary ====
            var updateDefs = new List<UpdateDefinition<Leave>>();
            var changes = new List<string>();
            bool anyChange = false;

            if (DateChanged(request.FromDate, leave.FromDate))
            {
                updateDefs.Add(Builders<Leave>.Update.Set(l => l.FromDate, request.FromDate!.Value.Date));
                changes.Add($"FromDate: {D(leave.FromDate)} → {D(request.FromDate!.Value)}");
                anyChange = true;
            }
            if (DateChanged(request.ToDate, leave.ToDate))
            {
                updateDefs.Add(Builders<Leave>.Update.Set(l => l.ToDate, request.ToDate!.Value.Date));
                changes.Add($"ToDate: {D(leave.ToDate)} → {D(request.ToDate!.Value)}");
                anyChange = true;
            }
            if (StringChanged(request.Reason, leave.Reason))
            {
                updateDefs.Add(Builders<Leave>.Update.Set(l => l.Reason, request.Reason!.Trim()));
                changes.Add($"Reason changed");
                anyChange = true;
            }
            if (StringChanged(request.SubType, leave.SubType))
            {
                updateDefs.Add(Builders<Leave>.Update.Set(l => l.SubType, request.SubType!.Trim()));
                changes.Add($"SubType: {leave.SubType} → {request.SubType}");
                anyChange = true;
            }
            if (StringChanged(request.Category, leave.Category))
            {
                updateDefs.Add(Builders<Leave>.Update.Set(l => l.Category, request.Category!.Trim()));
                changes.Add($"Category: {leave.Category} → {request.Category}");
                anyChange = true;
            }
            if (BoolChanged(request.IsCompOff, leave.IsCompOff))
            {
                updateDefs.Add(Builders<Leave>.Update.Set(l => l.IsCompOff, request.IsCompOff!.Value));
                changes.Add($"IsCompOff: {leave.IsCompOff} → {request.IsCompOff}");
                anyChange = true;
            }
            if (BoolChanged(request.IsHalfDay, leave.IsHalfDay))
            {
                updateDefs.Add(Builders<Leave>.Update.Set(l => l.IsHalfDay, request.IsHalfDay!.Value));
                changes.Add($"IsHalfDay: {leave.IsHalfDay} → {request.IsHalfDay}");
                anyChange = true;
            }
            if (ListDateChanged(request.CompDates, leave.CompDates))
            {
                var newComp = (request.CompDates ?? Enumerable.Empty<string>())
                    .Select(DateTime.Parse).Select(d => d.Date).OrderBy(d => d).ToList();
                updateDefs.Add(Builders<Leave>.Update.Set(l => l.CompDates, newComp));
                changes.Add("CompDates updated");
                anyChange = true;
            }

            //update documents in any case
            if (request.Documents != null)
            {
                updateDefs.Add(Builders<Leave>.Update.Set("Documents", storedDocs));
                changes.Add($"Modified document(s)");
                anyChange = true;
            }

            if (!anyChange)
                return Ok(new { success = true, leaveId = leave.Id.ToString(), noChange = true });

            // ==== 6. Apply meta updates ====
            updateDefs.Add(Builders<Leave>.Update.Set(l => l.UpdatedAt, DateTime.UtcNow.ToIST()));
            updateDefs.Add(Builders<Leave>.Update.Set(l => l.Status, "PENDING"));

            var changeSummary = changes.Count == 0 ? "No field changes detected" : string.Join("; ", changes);

            var trackingEntry = new RequestStatusTracking
            {
                ProcessedBy = $"{processingEmp?.FirstName} {processingEmp?.LastName}".Trim(),
                ProcessedById = processingEmp.Id,
                Status = "PENDING",
                Comment = $"Edited by {processingEmp?.FirstName} {processingEmp?.LastName}. Changes: {changeSummary}",
                ProcessedAt = DateTime.UtcNow.ToIST()
            };

            if (leave.ReqStatusTracking == null || !leave.ReqStatusTracking.Any())
                updateDefs.Add(Builders<Leave>.Update.Set(l => l.ReqStatusTracking, new List<RequestStatusTracking> { trackingEntry }));
            else
                updateDefs.Add(Builders<Leave>.Update.Push(l => l.ReqStatusTracking, trackingEntry));

            var update = Builders<Leave>.Update.Combine(updateDefs);
            var result = await _leaveCollection.UpdateOneAsync(
                Builders<Leave>.Filter.Eq(l => l.Id, leaveObjectId),
                update
            );

            if (result.ModifiedCount == 0)
                return StatusCode(500, new { error = "Failed to update request" });

            // ==== 7. Balance revert (if leave was previously approved) ====
            if (leave.Status.Trim().ToUpperInvariant() == "APPROVED")
            {
                double daysToRevert=0;
                //    = leave.Type.Trim().ToUpperInvariant() switch
                //{
                //    "LEAVE" => (leave.ToDate.Date - leave.FromDate.Date).Days + 1,
                //    "WFH" => ((leave.ToDate.Date - leave.FromDate.Date).Days + 1) * 0.5,
                //    _ => 0
                //};
                if(leave.Type.Trim().ToUpperInvariant()=="LEAVE" && !leave.IsHalfDay)
                {
                    daysToRevert = (leave.ToDate.Date - leave.FromDate.Date).Days + 1;

                }
                else if (leave.Type.Trim().ToUpperInvariant() == "LEAVE" && leave.IsHalfDay)
                {
                    daysToRevert = ((leave.ToDate.Date - leave.FromDate.Date).Days + 1) / 2;
                }

                if (daysToRevert > 0)
                {
                    var balanceRevertUpdate = Builders<Employee>.Update.Inc(e => e.Balance, (decimal)daysToRevert);
                    await _employeeCollection.UpdateOneAsync(e => e.Id == leave.UserId, balanceRevertUpdate);
                }
            }

            // ==== 8. Notify (if previously approved/denied) ====
            if (leave.Status.Equals("approved", StringComparison.OrdinalIgnoreCase) ||
                leave.Status.Equals("denied", StringComparison.OrdinalIgnoreCase) || leave.Status.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
            {
                var fcmTokens = await GetTargetFcmMessages(leave.UserId, leave.UserId, leave.Id.ToString());
                string typeLabel = leave.Type.Trim().ToUpperInvariant() switch
                {
                    "LEAVE" => "Leave",
                    "WFH" => "Work From Home",
                    "EXTRA" => "Extra Working Day",
                    "HALFDAY" => "Half Day Leave",
                    _ => "Leave"
                };

                var editor = await _employeeCollection.Find(e => e.Id == leave.UserId).FirstOrDefaultAsync();

                if (fcmTokens.Any())
                {
                    if (leave.Status.Equals("cancelled", StringComparison.OrdinalIgnoreCase))
                    {
                        await _fcmService.SendPushNotificationsAsync(
                   fcmTokens,
                   $"{typeLabel} Notification",
                   $"{editor.FirstName} {editor.LastName} has requested {typeLabel.ToLower()}");
                    }
                    else
                    {
                        await _fcmService.SendPushNotificationsAsync(
                            fcmTokens,
                            $"{typeLabel} Request Changed",
                            $"{editor.FirstName} {editor.LastName} edited a previously {leave.Status.ToUpperInvariant()} request."
                        );
                    }
                }

                await _notificationService.AddNotificationAsync(new AnnouncementRequest
                {
                    Title = $"{typeLabel} Request Changed",
                    Body = $"{editor.FirstName} {editor.LastName} edited a previously {leave.Status.ToUpperInvariant()} request.",
                    Screen = "Leave",
                    Type = "NOTIFICATION",
                    SentBy = editor.Id.ToString(),
                    UserId = editor.Id.ToString(),
                    LeaveId = leaveObjectId.ToString()
                });

                await SendLeaveEmailAsync(leave.Id.ToString());
            }

            // ==== 9. Final response ====
            return Ok(new { success = true, leaveId = leave.Id.ToString(), changes = changes });
        }

        [HttpPost("sendReminder")]
        public async Task<IActionResult> SendRequestReminder([FromBody] ReminderRequest request)
        {
            var requester = HttpContext.GetCurrentEmployee();
            if (requester == null)
            {
                return Unauthorized(new { error = "Authentication required" });
            }

            _logger.LogInformation("{Action} called by UserId: {UserId} with payload: {@Payload}",
                nameof(SendRequestReminder), requester.Id, request);

            if (!ObjectId.TryParse(request.LeaveId, out var leaveObjectId))
                return BadRequest(new { error = "Invalid leaveId" });

            if (!ObjectId.TryParse(request.UserId, out var userObjectId))
                return BadRequest(new { error = "Invalid userId" });

            var leave = await _leaveCollection.Find(l => l.Id == leaveObjectId).FirstOrDefaultAsync();

            if (leave == null)
                return NotFound(new { error = "Leave not found." });

            //if (leave.ReminderDetails?.ReminderCount >= 2)
            //    return BadRequest(new { error = "Reminder limit reached. No further reminders allowed." });

            // Update reminderDetails
            var updatedReminder = new ReminderDetails
            {
                ReminderSentAt = DateTime.UtcNow.ToIST(),
                ReminderCount = (leave.ReminderDetails?.ReminderCount ?? 0) + 1
            };
            var trackingEntry = new RequestStatusTracking
            {
                Status = "REMINDER SENT",
                ProcessedBy = $"{requester.FirstName} {requester.LastName}".Trim(),
                ProcessedById = requester.Id,
                ProcessedAt = DateTime.UtcNow.ToIST(),
                Comment = $"{requester.FirstName} {requester.LastName} sent the REMINDER for {leave.Type} request"
            };
            var update = Builders<Leave>.Update
                .Set(l => l.ReminderDetails, updatedReminder)
                .Set(l => l.UpdatedAt, DateTime.UtcNow.ToIST())
                .Push(l => l.ReqStatusTracking, trackingEntry); ;

            var result = await _leaveCollection.UpdateOneAsync(l => l.Id == leaveObjectId, update);
            if (result.ModifiedCount == 0)
                return StatusCode(500, new { error = "Failed to update request" });

            var fcmTokens = await GetTargetFcmMessages(userObjectId, userObjectId, leaveObjectId.ToString());
            string typeLabel = leave.Type.Trim().ToUpperInvariant() switch
            {
                "LEAVE" => "Leave",
                "WFH" => "Work From Home",
                "EXTRA" => "Extra Working Day",
                "HALFDAY" => "Half Day Leave",
                _ => "Leave"
            };

            // Call your Firebase notification service
            if (fcmTokens.Any())
            {
                await _fcmService.SendPushNotificationsAsync(
                    fcmTokens,
                    $"{typeLabel} Follow-up",
                    $"{requester.FirstName} {requester.LastName} just sent a follow-up on their leave request"
                );

                await _notificationService.AddNotificationAsync(new AnnouncementRequest
                {
                    Title = $"{typeLabel} Follow-up",
                    Body = $"{requester.FirstName} {requester.LastName} just sent a follow-up on their leave request",
                    Screen = "Leave",
                    Type = "NOTIFICATION",
                    SentBy = leave.UserId.ToString(),
                    UserId = leave.UserId.ToString(),
                    LeaveId = leaveObjectId.ToString()
                });
            }

            return Ok(new
            {
                success = true,
                message = "Reminder sent successfully.",
                reminderDetails = updatedReminder
            });
        }

        [HttpPost("cancel")]
        public async Task<IActionResult> CancelLeave([FromBody] RequestParam requestParam)
        {
            var currentEmployee = HttpContext.GetCurrentEmployee();
            if (currentEmployee == null)
            {
                return Unauthorized(new { error = "Authentication required" });
            }

            _logger.LogInformation("{Action} called by UserId: {UserId} with payload: {@Payload}",
                nameof(CancelLeave), currentEmployee.Id, requestParam);

            if (string.IsNullOrWhiteSpace(requestParam.LeaveId) || !ObjectId.TryParse(requestParam.LeaveId, out var leaveObjectId))
                return BadRequest(new { error = "Invalid LeaveId" });

            var leave = await _leaveCollection.Find(l => l.Id == leaveObjectId).FirstOrDefaultAsync();
            if (leave == null)
                return NotFound(new { error = "Leave request not found" });

            if (leave.Status == "CANCELLED")
                return BadRequest(new { error = "Leave request is already cancelled" });

            var update = Builders<Leave>.Update
                .Set(l => l.Status, "CANCELLED")
                .Set(l => l.UpdatedAt, DateTime.UtcNow.ToIST());

            await _leaveCollection.UpdateOneAsync(l => l.Id == leaveObjectId, update);

            if (leave.Status.Trim().ToUpperInvariant() == "APPROVED" && DateTime.UtcNow.ToIST().Date < leave.FromDate.Date)
            {
                decimal daysToRevert = 0m;
                var type = leave.Type.Trim().ToUpperInvariant();

                if (type == "LEAVE")
                {
                    //double ComputeDaysToAdjust(Leave lv, HashSet<DateTime> holidaySetLocal)
                    //{
                    //    var type = (lv.Type ?? "").Trim().ToUpperInvariant();

                    //    if (type == "WFH" || type == "EXTRA")
                    //        return 0; // no balance change

                    //    var workingDays = GetWorkingDaysInclusive(
                    //        lv.FromDate.Date, lv.ToDate.Date, holidaySetLocal);

                    //    if (type == "HALFDAY")
                    //        return workingDays > 0 ? 0.5 : 0.0;

                    //    // LEAVE or others -> full working days
                    //    return workingDays;
                    //}
                    decimal ComputeDaysToAdjust(Leave leave, HashSet<DateTime> holidaySet)
                    {
                        var type = (leave.Type ?? "").Trim().ToUpperInvariant();

                        bool isExtra = false;

                        if (type.ToLower() == "extra")
                        {
                            isExtra = true;
                        }
                        // Always decimal literals
                        if (type == "WFH")
                            return 0m;

                        decimal workingDays = GetWorkingDaysInclusive(
                            leave.FromDate.Date,
                            leave.ToDate.Date,
                            holidaySet,
                            isExtra
                        );

                        if (leave.IsHalfDay)
                        {
                            workingDays = workingDays / 2;
                        }
                        if (workingDays <= 0 && !leave.IsHalfDay)
                            return 0m;

                        // Explicit conversion
                        return (decimal)workingDays;
                    }
                    var applicant = await _employeeCollection.Find(e => e.Id == leave.UserId).FirstOrDefaultAsync();
                    var orgIdString = applicant.Organization.ToString();

                    var holidaySet = await BuildHolidaySet(
                orgIdString, leave.FromDate.Date, leave.ToDate.Date, includeOptional: false);

                    daysToRevert = ComputeDaysToAdjust(leave, holidaySet);
                    //daysToRevert = (leave.ToDate.Date - leave.FromDate.Date).Days + 1;
                }
                else if (type == "WFH")
                {
                    daysToRevert = 0m;
                }

                if (daysToRevert > 0)
                {
                    var balanceRevertUpdate = Builders<Employee>.Update.Inc(e => e.Balance, (decimal)daysToRevert);
                    await _employeeCollection.UpdateOneAsync(e => e.Id == leave.UserId, balanceRevertUpdate);
                }

                if (type == "LEAVE" || type == "WFH")
                {
                    var fcmTokens = await GetTargetFcmMessages(leave.UserId, leave.UserId, leave.Id.ToString());
                    string typeLabel = leave.Type.Trim().ToUpperInvariant() switch
                    {
                        "LEAVE" => "Leave",
                        "WFH" => "Work From Home",
                        _ => "Leave"
                    };

                    var employeeFilter = Builders<Employee>.Filter.In(e => e.Id, new[] { leave.UserId });
                    var employeeList = await _employeeCollection.Find(employeeFilter).ToListAsync();

                    var editer = employeeList.FirstOrDefault(e => e.Id == leave.UserId);

                    // Call your Firebase notification service
                    if (fcmTokens.Any())
                    {
                        await _fcmService.SendPushNotificationsAsync(
                            fcmTokens,
                            $"{typeLabel} Request cancelled",
                            $"{editer.FirstName} {editer.LastName} has cancelled previously Approved {typeLabel.ToLower()} request.The deducted days have been credited back."

                        );

                        await _notificationService.AddNotificationAsync(new AnnouncementRequest
                        {
                            Title = $"{typeLabel} Request cancelled",
                            Body = $"{editer.FirstName} {editer.LastName} has cancelled previously Approved {typeLabel.ToLower()} request.The deducted days have been credited back.",
                            Screen = "Leave",
                            Type = "NOTIFICATION",
                            SentBy = leave.UserId.ToString(),
                            UserId = leave.UserId.ToString(),
                            LeaveId = leaveObjectId.ToString()
                        });
                    }
                }
            }


            return Ok(new { success = true, message = "Request has been cancelled successfully." });
        }
        //[HttpPost("getall")]
        //public async Task<IActionResult> GetAllLeavesOfTeams([FromBody] RequestParam request)
        //{
        //    // Step 1: JWT validation
        //    var requester = await _employeeCollection.Find(e => e.JwtToken == request.JWTToken).FirstOrDefaultAsync();
        //    if (requester == null)
        //        return Unauthorized(new { error = "Invalid or expired JWT token." });

        //    // Step 2: Validate manager UserId
        //    if (string.IsNullOrWhiteSpace(request.UserId) || !ObjectId.TryParse(request.UserId, out var managerObjectId))
        //        return BadRequest(new { error = "Invalid manager UserId" });

        //    // Step 3: Get employees reporting to the manager
        //    var teamMembers = await _employeeCollection
        //        .Find(e => e.ReportingTo.Contains(managerObjectId))
        //        .ToListAsync();

        //    if (!teamMembers.Any())
        //        return Ok(new { upcomingLeaves = new List<object>() });

        //    var teamUserIds = teamMembers.Select(e => e.Id).ToList();
        //    var roleIds = teamMembers.Select(e => e.Role).Distinct().ToList();

        //    // Step 4: Resolve role ObjectIds to names
        //    var roleDocs = await _rolesCollection
        //        .Find(r => roleIds.Contains(r.Id))
        //        .ToListAsync();

        //    var roleMap = roleDocs.ToDictionary(r => r.Id, r => r.role);

        //    // Step 5: Fetch upcoming leaves
        //    var today = DateTime.UtcNow.Date;
        //    var leaveFilter = Builders<Leave>.Filter.And(
        //        Builders<Leave>.Filter.In(l => l.UserId, teamUserIds),
        //        Builders<Leave>.Filter.Gte(l => l.ToDate, today)
        //    );

        //    var leaves = await _leaveCollection.Find(leaveFilter).ToListAsync();

        //    // Step 6: Build final response
        //    var employeeMap = teamMembers.ToDictionary(e => e.Id, e => new
        //    {
        //        e.Role,
        //        e.ProfileImagePath,
        //        e.FirstName,
        //        e.LastName
        //    });

        //    var result = leaves.Select(l =>
        //    {
        //        var emp = employeeMap[l.UserId];
        //        var roleName = roleMap.TryGetValue(emp.Role, out var rName) ? rName : "Unknown";
        //        var profileImage = emp.ProfileImagePath ?? "";
        //        var firstName = emp.FirstName;
        //        var lastName = emp.LastName;

        //        return new
        //        {
        //            leaveId = l.Id.ToString(),
        //            profileImage,
        //            firstName,
        //            lastName,
        //            role = roleName,
        //            startDate = l.FromDate,
        //            endDate = l.ToDate,
        //            reason = l.Reason,
        //            status = l.Status,
        //            createdAt = l.CreatedAt,
        //            documentsCount = l.Documents?.Count ?? 0
        //        };
        //    });

        //    return Ok(new { result });


        //}

        //    [HttpPost("getall")]
        //    public async Task<IActionResult> GetAllLeavesOfTeams([FromBody] RequestParam request)
        //    {
        //        // 1) JWT validation
        //        var requester = await _employeeCollection
        //            .Find(e => e.JwtToken == request.JWTToken)
        //            .FirstOrDefaultAsync();

        //        if (requester == null)
        //            return Unauthorized(new { error = "Invalid or expired JWT token." });

        //        // 2) Resolve target user (manager/admin/hr). If not provided, use requester.
        //        ObjectId targetUserId;
        //        if (string.IsNullOrWhiteSpace(request.UserId))
        //        {
        //            targetUserId = requester.Id;
        //        }
        //        else if (!ObjectId.TryParse(request.UserId, out targetUserId))
        //        {
        //            return BadRequest(new { error = "Invalid manager UserId" });
        //        }

        //        // 3) Load target employee & role name
        //        var targetEmp = await _employeeCollection
        //            .Find(e => e.Id == targetUserId)
        //            .FirstOrDefaultAsync();
        //        if (targetEmp == null)
        //            return NotFound(new { error = "Target user not found" });

        //        var targetRoleDoc = await _rolesCollection
        //            .Find(r => r.Id == targetEmp.Role)
        //            .FirstOrDefaultAsync();
        //        var targetRoleName = targetRoleDoc?.role?.Trim() ?? string.Empty;

        //        // Elevated roles who can see everything
        //        var elevated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        //{
        //    "HR", "Admin", "SuperAdmin"
        //};

        //        // 4) Get employees in scope
        //        List<Employee> employeesInScope;
        //        if (elevated.Contains(targetRoleName))
        //        {
        //            // ALL employees (optionally filter out inactive if you want)
        //            employeesInScope = await _employeeCollection
        //                .Find(_ => true) // or e => e.IsActive
        //                .ToListAsync();
        //        }
        //        else
        //        {
        //            // Only team members reporting to the manager
        //            employeesInScope = await _employeeCollection
        //                .Find(e => e.ReportingTo.Contains(targetUserId))
        //                .ToListAsync();

        //            if (employeesInScope.Count == 0)
        //                return Ok(new { result = new List<object>() });
        //        }

        //        var teamUserIds = employeesInScope.Select(e => e.Id).ToList();
        //        var roleIds = employeesInScope.Select(e => e.Role).Distinct().ToList();

        //        // 5) Resolve role ObjectIds -> names once
        //        var roleDocs = await _rolesCollection
        //            .Find(r => roleIds.Contains(r.Id))
        //            .ToListAsync();
        //        var roleMap = roleDocs.ToDictionary(r => r.Id, r => r.role);

        //        // 6) Upcoming leaves only (end date today or later, in UTC)
        //        var today = DateTime.UtcNow.ToIST().Date;
        //        var leaveFilter = Builders<Leave>.Filter.And(
        //            Builders<Leave>.Filter.In(l => l.UserId, teamUserIds),
        //            Builders<Leave>.Filter.Gte(l => l.ToDate, today),
        //            Builders<Leave>.Filter.Ne(l => l.Status, "CANCELLED")

        //        );
        //        var leaves = await _leaveCollection.Find(leaveFilter).Sort(Builders<Leave>.Sort.Descending(l => l.CreatedAt)).ToListAsync();

        //        // 7) Employee lookup map for quick merge
        //        var employeeMap = employeesInScope.ToDictionary(e => e.Id, e => new
        //        {
        //            e.Role,
        //            e.ProfileImagePath,
        //            e.FirstName,
        //            e.LastName,
        //            e.Designation
        //        });

        //        // 8) Build response
        //        var result = leaves.Select(l =>
        //        {
        //            var emp = employeeMap[l.UserId];
        //            var roleName = roleMap.TryGetValue(emp.Role, out var rName) ? rName : "Unknown";

        //            return new
        //            {
        //                leaveId = l.Id.ToString(),
        //                profileImage = emp.ProfileImagePath ?? "",
        //                firstName = emp.FirstName,
        //                lastName = emp.LastName,
        //                role = roleName,
        //                startDate = l.FromDate.ToIST(),
        //                endDate = l.ToDate,
        //                reason = l.Reason,
        //                status = l.Status,
        //                Escalated = l.IsEscalated,
        //                createdAt = l.CreatedAt.ToIST(),
        //                documentsCount = l.Documents?.Count ?? 0,
        //                designation = emp.Designation
        //            };
        //        });

        //        return Ok(new { result });
        //    }

        // === Helpers (inside Auth/Leave controller class) ===
        private static string Norm(string? s) => (s ?? "").Trim().ToUpperInvariant();

        private static (string First, string Last) SplitName(string? full)
        {

            var p = (full ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (p.Length == 0) return ("", "");
            if (p.Length == 1) return (p[0], "");
            return (p[0], p[^1]); // first and last token
        }

        private static bool NameMatches(string processedBy, string reqFirst, string reqLast)
        {
            var (pf, pl) = SplitName(processedBy);
            return Norm(pf) == Norm(reqFirst) && Norm(pl) == Norm(reqLast);
        }

        private static string? MapAction(string? status)
        {
            switch (Norm(status))
            {
                case "APPROVED": return "Approved";
                case "REJECTED":
                case "DENIED": return "Rejected";
                default: return "";
            }
        }

        // === Endpoint ===
        [HttpPost("getall")]
        public async Task<IActionResult> GetAllLeavesOfTeams([FromBody] RequestParam request)
        {
            // 1) JWT validation
            var requester = HttpContext.GetCurrentEmployee();
            if (requester == null)
            {
                return Unauthorized(new { error = "Authentication required" });
            }

            _logger.LogInformation("{Action} called by UserId: {UserId} with payload: {@Payload}",
                nameof(GetAllLeavesOfTeams), requester.Id, request);

            var reqFirst = requester.FirstName ?? "";
            var reqLast = requester.LastName ?? "";

            // 2) Resolve target user (manager/admin/hr). If not provided, use requester.
            ObjectId targetUserId;
            if (string.IsNullOrWhiteSpace(request.UserId))
            {
                targetUserId = requester.Id;
            }
            else if (!ObjectId.TryParse(request.UserId, out targetUserId))
            {
                return BadRequest(new { error = "Invalid manager UserId" });
            }

            // 3) Load target employee & role name
            var targetEmp = await _employeeCollection
                .Find(e => e.Id == targetUserId)
                .FirstOrDefaultAsync();
            if (targetEmp == null)
                return NotFound(new { error = "Target user not found" });

            var targetRoleDoc = await _rolesCollection
                .Find(r => r.Id == targetEmp.Role)
                .FirstOrDefaultAsync();
            var targetRoleName = targetRoleDoc?.role?.Trim() ?? string.Empty;

            // Elevated roles who can see everything
            var elevated = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "HR", "Admin", "SuperAdmin"
    };

            // 4) Get employees in scope
            List<Employee> employeesInScope;
            if (elevated.Contains(targetRoleName))
            {
                employeesInScope = await _employeeCollection
                    .Find(_ => true)
                    .ToListAsync();
            }
            else
            {
                // Only team members reporting to the manager
                employeesInScope = await _employeeCollection
                    .Find(e => e.ReportingTo != null && e.ReportingTo.Contains(targetUserId))
                    .ToListAsync();

                if (employeesInScope.Count == 0)
                    return Ok(new { result = new List<object>() });
            }

            var teamUserIds = employeesInScope.Select(e => e.Id).ToList();
            var roleIds = employeesInScope.Select(e => e.Role).Distinct().ToList();

            // 5) Resolve role ObjectIds -> names once
            var roleDocs = await _rolesCollection
                .Find(r => roleIds.Contains(r.Id))
                .ToListAsync();
            var roleMap = roleDocs.ToDictionary(r => r.Id, r => r.role);

            // 6) Leave filter logic:
            // - All PENDING leaves (no date restriction)
            // - APPROVED / REJECTED only for today and future
            // - Exclude CANCELLED always

            var today = DateTime.UtcNow.ToIST().Date;

            var baseFilter = Builders<Leave>.Filter.In(l => l.UserId, teamUserIds);
            var notCancelled = Builders<Leave>.Filter.Ne(l => l.Status, "CANCELLED");

            // pending: include all
            var pendingFilter = Builders<Leave>.Filter.Eq(l => l.Status, "PENDING");

            // approved/rejected: only current or future
            var approvedRejectedFilter = Builders<Leave>.Filter.And(
                Builders<Leave>.Filter.In(l => l.Status, new[] { "APPROVED", "REJECTED" }),
                Builders<Leave>.Filter.Gte(l => l.ToDate, today)
            );

            // combine
            var leaveFilter = Builders<Leave>.Filter.And(
                baseFilter,
                notCancelled,
                Builders<Leave>.Filter.Or(pendingFilter, approvedRejectedFilter)
            );

            var leaves = await _leaveCollection
                .Find(leaveFilter)
                .Sort(Builders<Leave>.Sort.Descending(l => l.CreatedAt))
                .ToListAsync();


            // 7) Employee lookup map for quick merge
            var employeeMap = employeesInScope.ToDictionary(e => e.Id, e => new
            {
                e.Role,
                e.ProfileImagePath,
                e.FirstName,
                e.LastName,
                e.Designation
            });

            // 8) Build response (adds actionTaken)
            var result = leaves.Select(l =>
            {
                var emp = employeeMap[l.UserId];
                var roleName = roleMap.TryGetValue(emp.Role, out var rName) ? rName : "Unknown";

                string? actionTaken = string.Empty;
                string? latestStatus = string.Empty;
                if (l.ReqStatusTracking != null && l.ReqStatusTracking.Count > 0)
                {
                    var latestEntry = l.ReqStatusTracking
           .OrderByDescending(t => t.ProcessedAt)
           .FirstOrDefault();

                    latestStatus = latestEntry?.Status;

                    if (latestStatus.Trim().ToLower() != "pending")
                    {
                        var latestByRequester = l.ReqStatusTracking
                                       .Where(t => NameMatches(t.ProcessedBy ?? "", reqFirst, reqLast))
                                       .OrderByDescending(t => t.ProcessedAt)
                                       .FirstOrDefault();

                        actionTaken = MapAction(latestByRequester?.Status);
                    }
                }

                return new
                {
                    leaveId = l.Id.ToString(),
                    profileImage = emp.ProfileImagePath ?? "",
                    firstName = emp.FirstName,
                    lastName = emp.LastName,
                    role = roleName,
                    startDate = l.FromDate.ToIST(),
                    endDate = l.ToDate,
                    reason = l.Reason,
                    status = l.Status,
                    Escalated = l.IsEscalated,
                    createdAt = l.CreatedAt.ToIST(),
                    documentsCount = l.Documents?.Count ?? 0,
                    designation = emp.Designation,
                    actionTaken = actionTaken,
                    userId = l.UserId.ToString(),
                    isHalfDay = l.IsHalfDay,
                    requestType = l.Type
                };
            });

            return Ok(new { result });
        }

        [HttpPost("showpending")]
        public async Task<IActionResult> ShowPendingLeaves([FromBody] RequestParam request)
        {
            var currentEmployee = HttpContext.GetCurrentEmployee();
            if (currentEmployee == null)
            {
                return Unauthorized(new { error = "Authentication required" });
            }

            _logger.LogInformation("{Action} called by UserId: {UserId} with payload: {@Payload}",
                nameof(ShowPendingLeaves), currentEmployee.Id, request);

            // Step 1: Fetch all pending leaves
            var leaveFilter = Builders<Leave>.Filter.Eq(l => l.Status, "PENDING");
            var pendingLeaves = await _leaveCollection.Find(leaveFilter).ToListAsync();

            if (!pendingLeaves.Any())
                return Ok(new { pendingLeaves = new List<object>() });

            // Step 2: Get unique userIds from leaves
            var applicantIds = pendingLeaves.Select(l => l.UserId).Distinct().ToList();

            // Step 3: Fetch employee info for those userIds
            var employeeFilter = Builders<Employee>.Filter.In(e => e.Id, applicantIds);
            var employees = await _employeeCollection.Find(employeeFilter).ToListAsync();

            var employeeNameMap = employees.ToDictionary(
                e => e.Id,
                e => $"{e.FirstName} {e.LastName}"
            );

            // Step 4: Build response with full names and decrypted documents
            var response = new List<object>();

            foreach (var leave in pendingLeaves)
            {
                response.Add(new
                {
                    leaveId = leave.Id.ToString(),
                    EmployeeName = employeeNameMap.TryGetValue(leave.UserId, out var fullName) ? fullName : "Unknown",
                    leave.Type,
                    FromDate = leave.FromDate.ToIST(),
                    ToDate = leave.ToDate.ToIST(),
                    leave.Reason,
                    leave.Status,
                    leave.IsHalfDay,
                    leave.IsCompOff,
                    leave.CompDates
                });
            }

            return Ok(new { pendingLeaves = response });
        }

        [HttpPost("getleavedoc")]
        public async Task<IActionResult> GetLeaveDocuments([FromBody] RequestParam requestParam)
        {
            var currentEmployee = HttpContext.GetCurrentEmployee();
            if (currentEmployee == null)
            {
                return Unauthorized(new { error = "Authentication required" });
            }

            _logger.LogInformation("{Action} called by UserId: {UserId} with payload: {@Payload}",
                nameof(GetLeaveDocuments), currentEmployee.Id, requestParam);

            if (string.IsNullOrWhiteSpace(requestParam.LeaveId) || !ObjectId.TryParse(requestParam.LeaveId, out var leaveObjectId))
                return BadRequest(new { error = "Invalid LeaveId" });

            var leave = await _leaveCollection.Find(l => l.Id == leaveObjectId).FirstOrDefaultAsync();
            if (leave == null)
                return NotFound(new { error = "Leave not found." });

            var docList = new List<object>();

            if (leave.Documents != null)
            {
                foreach (var doc in leave.Documents)
                {
                    var baseStoragePath = Environment.GetEnvironmentVariable("STORAGE_ROOT_PATH")
                      ?? "/data/leavify";
                    var fullPath = Path.Combine(baseStoragePath, doc.DocPath);
                    _logger.LogInformation($"Document storage path:{Path.Combine(baseStoragePath, doc.DocPath)}");

                    if (System.IO.File.Exists(fullPath))
                    {
                        docList.Add(new
                        {
                            doc.DocType,
                            DocUrl = doc.DocPath.Replace("\\", "/")
                        });
                    }
                    else
                    {
                        docList.Add(new
                        {
                            doc.DocType,
                            DocUrl = string.Empty
                        });
                    }
                }
            }

            return Ok(new { documents = docList });
        }

        [HttpPost("getleavesbyid/")]
        public async Task<IActionResult> GetLeaveDetails(RequestParam requestParam)
        {
            var employee = HttpContext.GetCurrentEmployee();
            if (employee == null)
            {
                return Unauthorized(new { error = "Authentication required" });
            }

            _logger.LogInformation("{Action} called by UserId: {UserId} with payload: {@Payload}",
                nameof(GetLeaveDetails), employee.Id, requestParam);

            if (!ObjectId.TryParse(requestParam.LeaveId, out var leaveObjectId))
                return BadRequest(new { error = "Invalid LeaveId" });

            var leave = await _leaveCollection
                .Find(l => l.Id == leaveObjectId)
                .FirstOrDefaultAsync();
            if (leave == null)
                return NotFound(new { error = "Leave not found" });

            var leaveemployee = await _employeeCollection
                .Find(e => e.Id == leave.UserId)
                .FirstOrDefaultAsync();
            if (leaveemployee == null)
                return NotFound(new { error = "Leave owner not found" });

            // ===== Working days / 20-days calc =====
            var year = leave.FromDate.Year.ToString();
            var holidayDoc = await _holidayCollection.Find(h => h.Year == year).FirstOrDefaultAsync();
            var nonOptionalHolidays = holidayDoc?.HolidayDates?
                .Where(h => !h.IsOptional)
                .Select(h => DateTime.Parse(h.Date).Date)
                .ToHashSet() ?? new HashSet<DateTime>();

            DateTime startDate = leave.FromDate.Date;
            DateTime endDate = leave.ToDate.Date;

            int workingDays = 0;
            for (var dt = startDate; dt <= endDate; dt = dt.AddDays(1))
            {
                if (dt.DayOfWeek != DayOfWeek.Saturday &&
                    dt.DayOfWeek != DayOfWeek.Sunday &&
                    !nonOptionalHolidays.Contains(dt))
                {
                    workingDays++;
                }
            }

            DateTime monthStart = new DateTime(startDate.Year, startDate.Month, 1);
            DateTime monthEnd = new DateTime(startDate.Year, startDate.Month, DateTime.DaysInMonth(startDate.Year, startDate.Month));

            int totalWorkingDaysInMonth = 0;
            for (var dt = monthStart; dt <= monthEnd; dt = dt.AddDays(1))
            {
                if (dt.DayOfWeek != DayOfWeek.Saturday &&
                    dt.DayOfWeek != DayOfWeek.Sunday &&
                    !nonOptionalHolidays.Contains(dt))
                {
                    totalWorkingDaysInMonth++;
                }
            }
            // === Deduct approved leave days in the same month ===


            var approvedLeavesThisMonth = await _leaveCollection
                .Find(l =>
                    l.UserId == leaveemployee.Id &&
                    l.Status == "APPROVED" &&
                    l.FromDate <= monthEnd &&
                    l.ToDate >= monthStart &&
                    l.Id != leave.Id)
                .ToListAsync();

            int approvedLeaveDays = 0;
            foreach (var l in approvedLeavesThisMonth)
            {
                var leaveStart = l.FromDate.Date < monthStart ? monthStart : l.FromDate.Date;
                var leaveEnd = l.ToDate.Date > monthEnd ? monthEnd : l.ToDate.Date;

                for (var dt = leaveStart; dt <= leaveEnd; dt = dt.AddDays(1))
                {
                    if (dt.DayOfWeek != DayOfWeek.Saturday &&
                        dt.DayOfWeek != DayOfWeek.Sunday &&
                        !nonOptionalHolidays.Contains(dt))
                    {
                        approvedLeaveDays++;
                    }
                }
            }

            int remainingWorkingDays = totalWorkingDaysInMonth - workingDays - approvedLeaveDays;
            bool willComplete20Days = remainingWorkingDays >= 20;

            // ===== Manager statuses (latest per reporting manager) =====
            var reportingManagerIds = (leaveemployee.ReportingTo ?? new List<ObjectId>()).Distinct().ToList();

            var managerDocs = await _employeeCollection
                .Find(e => reportingManagerIds.Contains(e.Id))
                .Project(e => new { e.Id, e.FirstName, e.LastName, e.ProfileImagePath })
                .ToListAsync();

            var managerInfoMap = managerDocs.ToDictionary(
                x => x.Id,
                x => new
                {
                    Name = $"{x.FirstName} {x.LastName}".Trim(),
                    Img = x.ProfileImagePath ?? string.Empty
                });

            // Build a helper to resolve "ProcessedBy" (could be name or ObjectId string) -> ObjectId
            var nameToId = managerDocs.ToDictionary(
                x => $"{x.FirstName} {x.LastName}".Trim(),
                x => x.Id,
                StringComparer.OrdinalIgnoreCase);

            bool TryResolveManagerId(string processedBy, out ObjectId id)
            {
                id = ObjectId.Empty;
                if (string.IsNullOrWhiteSpace(processedBy)) return false;

                // If stored as ObjectId string
                if (ObjectId.TryParse(processedBy, out id)) return true;

                // If stored as full name
                return nameToId.TryGetValue(processedBy.Trim(), out id);
            }

            var tracking = leave.ReqStatusTracking ?? new List<RequestStatusTracking>();

            // Resolve to ObjectId and group by that
            var resolved = new List<(ObjectId ManagerId, RequestStatusTracking Entry)>();
            foreach (var t in tracking)
            {
                if (TryResolveManagerId(t.ProcessedBy, out var mid))
                    resolved.Add((mid, t));
            }

            var latestByManager = resolved
                .GroupBy(x => x.ManagerId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(x => x.Entry)
                          .OrderByDescending(t => t.ProcessedAt)
                          .First()
                );

            var managerStatuses = reportingManagerIds.Select(mid =>
            {
                var hasEntry = latestByManager.TryGetValue(mid, out var entry);
                var latestStatus = hasEntry ? (entry.Status ?? "Pending") : "Pending";
                var lastActionAt = hasEntry ? (entry.ProcessedAt) : (DateTime?)null;

                managerInfoMap.TryGetValue(mid, out var md);

                return new
                {
                    ManagerId = mid.ToString(),
                    ManagerName = md?.Name ?? mid.ToString(),
                    ProfileImageUrl = md?.Img ?? string.Empty,
                    LatestStatus = latestStatus,
                    LastActionAt = lastActionAt,
                    IsPending = !hasEntry || latestStatus.Equals("Pending", StringComparison.OrdinalIgnoreCase),
                    IsCurrentUser = mid == employee.Id
                };
            }).ToList();

            var currentUserAction = managerStatuses.FirstOrDefault(x => x.IsCurrentUser) ?? new
            {
                ManagerId = employee.Id.ToString(),
                ManagerName = $"{employee.FirstName} {employee.LastName}".Trim(),
                ProfileImageUrl = employee.ProfileImagePath ?? string.Empty,
                LatestStatus = "NotApplicable",
                LastActionAt = (DateTime?)null,
                IsPending = false,
                IsCurrentUser = true
            };

            var pendingManagers = managerStatuses
                .Where(x => x.IsPending)
                .Select(x => new { x.ManagerId, x.ManagerName, x.ProfileImageUrl })
                .ToList();

            // ===== Recent approved leaves in last 3 months =====
            var threeMonthsAgo = DateTime.UtcNow.ToIST().AddMonths(-3);
            var recentApprovedLeaves = await _leaveCollection
                .Find(l => l.UserId == leaveemployee.Id &&
                           l.Status == "APPROVED" &&
                           l.FromDate >= threeMonthsAgo &&
                           l.Id != leave.Id)
                .Project(l => new
                {
                    type = l.Type,
                    fromDate = l.FromDate,
                    toDate = l.ToDate,
                    reason = l.Reason
                })
                .ToListAsync();

            // Calculate working window: 4 previous and 4 next working days (excluding Sat/Sun/holidays)
            //var allYears = new HashSet<string>();
            //for (var dt = leave.FromDate.AddDays(-10); dt <= leave.ToDate.AddDays(10); dt = dt.AddDays(1))
            //    allYears.Add(dt.Year.ToString());

            var allYears = new HashSet<string>();
            var scanStart = leave.FromDate.AddDays(-10);
            var scanEnd = leave.FromDate.AddDays(10);
            for (var dt = scanStart; dt <= scanEnd; dt = dt.AddDays(1))
                allYears.Add(dt.Year.ToString());

            // Fetch holiday documents for all years in the extended range
            var holidayDocs = await _holidayCollection.Find(
                Builders<HolidayList>.Filter.And(
                    Builders<HolidayList>.Filter.In(h => h.Year, allYears.ToList())
                )).ToListAsync();

            // Build holiday set (non-optional only), and handle exchangedWith
            var holidays = new HashSet<DateTime>();
            var exchangedWorking = new HashSet<DateTime>();
            foreach (var doc in holidayDocs)
            {
                foreach (var h in doc.HolidayDates)
                {
                    if (DateTime.TryParse(h.Date, out var hd))
                        holidays.Add(hd.Date);
                    if (!string.IsNullOrWhiteSpace(h.ExchangedWith) && DateTime.TryParse(h.ExchangedWith, out var ex))
                        exchangedWorking.Add(ex.Date);
                }
            }
            holidays.ExceptWith(exchangedWorking);

            // Build previous 4 working days
            var prevWorkingDays = new List<DateTime>();
            var dtPrev = leave.FromDate.AddDays(-1);
            while (prevWorkingDays.Count < 4)
            {
                if (dtPrev.DayOfWeek != DayOfWeek.Saturday &&
                    dtPrev.DayOfWeek != DayOfWeek.Sunday &&
                    !holidays.Contains(dtPrev.Date))
                {
                    prevWorkingDays.Add(dtPrev.Date);
                }
                dtPrev = dtPrev.AddDays(-1);
            }
            prevWorkingDays.Reverse(); // Chronological order

            // Build next 4 working days
            var nextWorkingDays = new List<DateTime>();
            var dtNext = leave.ToDate.AddDays(1);
            while (nextWorkingDays.Count < 4)
            {
                if (dtNext.DayOfWeek != DayOfWeek.Saturday &&
                    dtNext.DayOfWeek != DayOfWeek.Sunday &&
                    !holidays.Contains(dtNext.Date))
                {
                    nextWorkingDays.Add(dtNext.Date);
                }
                dtNext = dtNext.AddDays(1);
            }

            // Final window: leave period + previous/next working days
            var windowDates = new HashSet<DateTime>();
            for (var dt = leave.FromDate.Date; dt <= leave.ToDate.Date; dt = dt.AddDays(1))
            {
                if (dt.DayOfWeek != DayOfWeek.Saturday &&
                    dt.DayOfWeek != DayOfWeek.Sunday &&
                    !holidays.Contains(dt))
                {
                    windowDates.Add(dt);
                }
            }
            windowDates.UnionWith(prevWorkingDays);
            windowDates.UnionWith(nextWorkingDays);

            // Get all team members sharing at least one project, excluding self
            var teamMembers = await _employeeCollection
                .Find(e => e.ProjectList.Any(p => leaveemployee.ProjectList.Contains(p)) && e.Id != leaveemployee.Id)
                .Project(e => new { e.Id, e.FirstName, e.LastName, e.ProfileImagePath })
                .ToListAsync();

            var teamMemberIds = teamMembers.Select(e => e.Id).ToList();

            // Get approved leaves for team members that overlap with any window date
            var teamLeaves = await _leaveCollection
                .Find(l =>
                    teamMemberIds.Contains(l.UserId) &&
                    l.Status == "APPROVED" &&
                    (
                        windowDates.Any(d => d >= l.FromDate.Date && d <= l.ToDate.Date)
                    )
                ).ToListAsync();

            // Filter and map result
            var teamConflictingLeaves = teamLeaves
                .Select(l =>
                {
                    var emp = teamMembers.FirstOrDefault(e => e.Id == l.UserId);
                    return new
                    {
                        fName = emp?.FirstName ?? "",
                        lName = emp?.LastName ?? "",
                        ProfileImagePath = emp?.ProfileImagePath ?? "",
                        fromDate = l.FromDate,
                        toDate = l.ToDate,
                        reson = l.Reason
                    };
                })
                .ToList();

            decimal ComputeDaysToAdjust(Leave leave, HashSet<DateTime> holidaySet)
            {
                var type = (leave.Type ?? "").Trim().ToUpperInvariant();

                bool isExtra = false;

                if (type.ToLower() == "extra")
                {
                    isExtra = true;
                }

                // Always decimal literals
                if (type == "WFH")
                    return 0m;

                double workingDays = GetWorkingDaysInclusive(
                                    leave.FromDate.Date,
                                    leave.ToDate.Date,
                                    holidaySet,
                                    isExtra
                                );

                if (leave.IsHalfDay)
                {
                    workingDays = workingDays / 2;
                }
                if (workingDays <= 0 && !leave.IsHalfDay)
                    return 0m;

                // Explicit conversion
                return (decimal)workingDays;
            }

            var orgIdString = leaveemployee.Organization.ToString();

            var holidaySet = await BuildHolidaySet(
        orgIdString, leave.FromDate.Date, leave.ToDate.Date, includeOptional: false);

            var daysToDeduct = ComputeDaysToAdjust(leave, holidaySet);

            return Ok(new
            {
                UserId = leaveemployee.Id.ToString(),
                RequestedById = leave.RequestedBy.ToString(),
                LeaveId = leave.Id.ToString(),
                EmployeeName = $"{leaveemployee.FirstName} {leaveemployee.LastName}",
                WorkingDaysCount = remainingWorkingDays,
                BalanceLeaves = leaveemployee.Balance,
                Duration = daysToDeduct,
                LeaveDetails = new
                {
                    leave.Type,
                    SubType = leave.SubType,
                    CreatedAt = leave.CreatedAt,
                    FromDate = leave.FromDate.ToIST(),
                    ToDate = leave.ToDate,
                    Category = leave.Category,
                    leave.Reason,
                    leave.Documents,
                    leave.IsCompOff,
                    leave.CompDates,
                    leave.IsHalfDay,
                    leave.Status,
                    leave.IsEscalated,
                    ReqStatusTracking = leave.ReqStatusTracking?.Select(t => new
                    {
                        t.Status,
                        t.ProcessedBy,
                        ProcessedAt = t.ProcessedAt.ToIST(),
                        t.Comment
                    }).ToList(),
                    leave.EscalationDet,
                    UpdatedAt = leave.UpdatedAt,
                    leave.ReminderDetails
                },
                CurrentUserAction = currentUserAction,
                RecentApprovedLeaves = recentApprovedLeaves,
                TeamConflictingLeaves = teamConflictingLeaves
            });
        }
        //   [HttpPost("getleavesbyid/")]
        //   public async Task<IActionResult> GetLeaveDetails(RequestParam requestParam)
        //   {
        //       if (string.IsNullOrWhiteSpace(requestParam.JWTToken))
        //           return BadRequest(new { error = "JWTToken is required" });

        //       var employee = await _employeeCollection.Find(e => e.JwtToken == requestParam.JWTToken).FirstOrDefaultAsync();
        //       if (employee == null)
        //           return NotFound(new { error = "Invalid or expired JWT token." });

        //       if (!ObjectId.TryParse(requestParam.LeaveId, out var leaveObjectId))
        //           return BadRequest(new { error = "Invalid LeaveId" });

        //       var leave = await _leaveCollection.Find(l => l.Id == leaveObjectId).FirstOrDefaultAsync();
        //       if (leave == null)
        //           return NotFound(new { error = "Leave not found" });

        //       var leaveemployee = await _employeeCollection.Find(e => e.Id == leave.UserId).FirstOrDefaultAsync();

        //       var year = leave.FromDate.Year.ToString();

        //       var holidayDoc = await _holidayCollection.Find(h => h.Year == year).FirstOrDefaultAsync();
        //       var nonOptionalHolidays = holidayDoc?.HolidayDates
        //           .Where(h => !h.IsOptional)
        //           .Select(h => DateTime.Parse(h.Date))
        //           .ToHashSet() ?? new HashSet<DateTime>();

        //       DateTime startDate = leave.FromDate.Date;
        //       DateTime endDate = leave.ToDate.Date;

        //       int workingDays = 0;
        //       for (var dt = startDate; dt <= endDate; dt = dt.AddDays(1))
        //       {
        //           if (dt.DayOfWeek != DayOfWeek.Saturday && dt.DayOfWeek != DayOfWeek.Sunday && !nonOptionalHolidays.Contains(dt))
        //           {
        //               workingDays++;
        //           }
        //       }

        //       DateTime monthStart = new DateTime(startDate.Year, startDate.Month, 1);
        //       DateTime monthEnd = new DateTime(startDate.Year, startDate.Month, DateTime.DaysInMonth(startDate.Year, startDate.Month));

        //       int totalWorkingDaysInMonth = 0;
        //       for (var dt = monthStart; dt <= monthEnd; dt = dt.AddDays(1))
        //       {
        //           if (dt.DayOfWeek != DayOfWeek.Saturday && dt.DayOfWeek != DayOfWeek.Sunday && !nonOptionalHolidays.Contains(dt))
        //           {
        //               totalWorkingDaysInMonth++;
        //           }
        //       }

        //       int remainingWorkingDays = totalWorkingDaysInMonth - workingDays;
        //       bool willComplete20Days = remainingWorkingDays >= 20;
        //       var latestStatusByUser = leave.ReqStatusTracking?
        //.Where(r => r.Status == "APPROVED" || r.Status == "DENIED" || r.Status == "REJECTED")
        //.GroupBy(r => r.ProcessedBy.Trim(), StringComparer.OrdinalIgnoreCase)
        //.Select(g => g.OrderByDescending(x => x.ProcessedAt).First())
        //.OrderByDescending(r => r.ProcessedAt) // optional: sort newest first
        //.ToList() ?? new List<RequestStatusTracking>();

        //       var managerInfoMap = managerDocs.ToDictionary(
        //             x => x.Id,
        //             x => new
        //             {
        //                 Name = $"{x.FirstName} {x.LastName}".Trim(),
        //                 Img = x.ProfileImagePath ?? string.Empty
        //             });
        //       managerInfoMap.TryGetValue(mid, out var md);
        //       var reportingManagerIds = (leaveemployee.ReportingTo ?? new List<ObjectId>()).Distinct().ToList();


        //       var managerStatuses = reportingManagerIds.Select(mid =>
        //       {
        //           var hasEntry = latestByManager.TryGetValue(mid, out var entry);
        //           var latestStatus = hasEntry ? (entry.Status ?? "Pending") : "Pending";
        //           var lastActionAt = hasEntry ? (entry.ProcessedAt) : (DateTime?)null;

        //           var currentUserAction = managerStatuses.FirstOrDefault(x => x.IsCurrentUser) ?? new
        //       {
        //           ManagerId = employee.Id.ToString(),
        //           ManagerName = $"{employee.FirstName} {employee.LastName}".Trim(),
        //           ProfileImageUrl = employee.ProfileImagePath ?? string.Empty,
        //           LatestStatus = "NotApplicable",
        //           LastActionAt = (DateTime?)null,
        //           IsPending = false,
        //           IsCurrentUser = true
        //       };

        //       return Ok(new
        //       {
        //           UserId = leaveemployee.Id.ToString(),
        //           LeaveId = leave.Id.ToString(),
        //           EmployeeName = $"{leaveemployee.FirstName} {leaveemployee.LastName}",
        //           WillComplete20Days = willComplete20Days,
        //           BalanceLeaves = leaveemployee.Balance,
        //           CurrentUserAction = currentUserAction,
        //           LatestProcessedStatus = latestStatusByUser,
        //           LeaveDetails = new
        //           {
        //               // manually include only required fields
        //               leave.Type,
        //               leave.CreatedAt,
        //               leave.FromDate,
        //               leave.ToDate,
        //               leave.Reason,
        //               leave.Documents,
        //               leave.IsCompOff,
        //               leave.CompDates,
        //               leave.IsHalfDay,
        //               leave.Status,
        //               leave.IsEscalated,
        //               leave.ReqStatusTracking,
        //               leave.EscalationDet,
        //               leave.UpdatedAt,
        //               leave.ReminderDetails
        //           }
        //       });

        //   }

        [HttpPost("getmyleaves/")]
        public async Task<IActionResult> GetMyLeaves(RequestParam requestParam)
        {
            try
            {
                var employee = HttpContext.GetCurrentEmployee();
                if (employee == null)
                {
                    return Unauthorized(new { error = "Authentication required" });
                }

                _logger.LogInformation("{Action} called by UserId: {UserId} with payload: {@Payload}",
                    nameof(GetMyLeaves), employee.Id, requestParam);

                if (!ObjectId.TryParse(requestParam.UserId, out var parsedUserId))
                    return BadRequest(new { error = "Invalid userId" });

                var objectUserId = parsedUserId;

                var currentEmployee = await _employeeCollection.Find(e => e.Id == objectUserId).FirstOrDefaultAsync();
                if (currentEmployee == null)
                    return NotFound(new { error = "Employee not found." });

                // Step 1: Get all leaves for user
                var leaves = await _leaveCollection.Find(l => l.UserId == employee.Id).ToListAsync();

                // Step 2: Get employee to fetch balance


                // Step 3: Count by status (case-insensitive)
                var approved = leaves.Count(l => l.Status.Equals("APPROVED", StringComparison.OrdinalIgnoreCase));
                var pending = leaves.Count(l => l.Status.Equals("PENDING", StringComparison.OrdinalIgnoreCase));
                var rejected = leaves.Count(l => l.Status.Equals("REJECTED", StringComparison.OrdinalIgnoreCase));
                var cancelled = leaves.Count(l => l.Status.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase));

                return Ok(new
                {
                    UserId = currentEmployee.Id.ToString(),
                    BalanceLeaves = currentEmployee.Balance,
                    ApprovedLeaves = approved,
                    PendingLeaves = pending,
                    RejectedLeaves = rejected,
                    CancelledLeaves = cancelled,
                    AllLeaves = leaves.Select(l => new
                    {
                        Id = l.Id.ToString(),
                        l.Type,
                        FromDate = l.FromDate.ToIST(),
                        ToDate = l.ToDate,
                        l.Reason,
                        l.Status,
                        l.IsHalfDay,
                        l.IsCompOff,
                        CompDates = l.CompDates.Select(d => d.ToIST()).ToList(),
                        CreatedAt = l.CreatedAt.ToIST(),
                        UpdatedAt = l.UpdatedAt.ToIST()
                    })
                });
            }
            catch (Exception ex)
            {
                throw;
            }
        }

        [HttpPost("processescalated")]
        public async Task<IActionResult> ProcessEscalatedRequest([FromBody] RequestParam requestParam)
        {
            var employee = HttpContext.GetCurrentEmployee();
            if (employee == null)
            {
                return Unauthorized(new { error = "Authentication required" });
            }

            _logger.LogInformation("{Action} called by UserId: {UserId} with payload: {@Payload}",
                nameof(ProcessEscalatedRequest), employee.Id, requestParam);

            if (!ObjectId.TryParse(requestParam.LeaveId, out var leaveObjectId))
                return BadRequest(new { error = "Invalid LeaveId" });

            var leave = await _leaveCollection.Find(l => l.Id == leaveObjectId).FirstOrDefaultAsync();
            if (leave == null)
                return NotFound(new { error = "Leave not found" });

            var applicant = await _employeeCollection.Find(e => e.Id == leave.UserId).FirstOrDefaultAsync();

            // Step 1: If reqStatusTracking is null, initialize it
            if (leave.ReqStatusTracking == null)
            {
                var setEmptyArray = Builders<Leave>.Update.Set(l => l.ReqStatusTracking, new List<RequestStatusTracking>());
                await _leaveCollection.UpdateOneAsync(l => l.Id == leaveObjectId, setEmptyArray);
            }

            var processingEmp = await _employeeCollection.Find(e => e.Id == employee.Id).FirstOrDefaultAsync();

            // Step 2: Update escalationDet and push tracking
            var update = Builders<Leave>.Update
            .Set(l => l.EscalationDet.EscalationStatus, "RESOLVED")
            .Set(l => l.IsEscalated, false)
            .Set(l => l.EscalationDet.Comments, requestParam.Comment)
            .Set(l => l.EscalationDet.ResolvedDate, DateTime.UtcNow.ToIST())
            .Set(l => l.UpdatedAt, DateTime.UtcNow.ToIST())
            .Push(l => l.ReqStatusTracking, new RequestStatusTracking
            {
                Status = "RESOLVED",
                ProcessedBy = $"{processingEmp?.FirstName} {processingEmp?.LastName}".Trim(),
                ProcessedById = processingEmp.Id,
                ProcessedAt = DateTime.UtcNow.ToIST(),
                Comment = requestParam.Comment
            });

            await _leaveCollection.UpdateOneAsync(l => l.Id == leaveObjectId, update);

            var fcmTokens = await GetTargetFcmMessages(leave.UserId, processingEmp.Id, leave.Id.ToString(), false);

            if (fcmTokens.Any())
            {
                await _fcmService.SendPushNotificationsAsync(
                    fcmTokens,
                    $"Escalated {leave.Type} Request Processed",
                    $"The escalated {leave.Type.ToLower()} request from {applicant?.FirstName} {applicant?.LastName} has been resolved."
                );

                await _notificationService.AddNotificationAsync(new AnnouncementRequest
                {
                    Title = $"Escalated {leave.Type} Request Processed",
                    Body = $"The escalated {leave.Type.ToLower()} request from {applicant?.FirstName} {applicant?.LastName} has been resolved.",
                    Screen = "Leave",
                    Type = "NOTIFICATION",
                    SentBy = leave.UserId.ToString(),
                    UserId = leave.UserId.ToString(),
                    LeaveId = leaveObjectId.ToString()
                });
            }

            return Ok(new
            {
                message = "Escalation resolved successfully.",
                resolvedBy = employee.Id.ToString(),
                comment = requestParam.Comment,
                success = true
            });
        }

        [HttpPost("getescalated")]
        public async Task<IActionResult> GetEscalatedRequest([FromBody] RequestParam requestParam)
        {
            var employee = HttpContext.GetCurrentEmployee();
            if (employee == null)
            {
                return Unauthorized(new { error = "Authentication required" });
            }

            _logger.LogInformation("{Action} called by UserId: {UserId} with payload: {@Payload}",
                nameof(GetEscalatedRequest), employee.Id, requestParam);
            // Step 1: Get all escalated leaves with status PENDING
            var filter = Builders<Leave>.Filter.Eq(l => l.EscalationDet.EscalationStatus, "PENDING");
            var leaves = await _leaveCollection.Find(filter).ToListAsync();

            // Step 2: Get userIds and resolve their Role ObjectIds
            var userIds = leaves.Select(l => l.UserId).Distinct().ToList();

            var users = await _employeeCollection
                .Find(Builders<Employee>.Filter.In(e => e.Id, userIds))
                .Project(e => new { e.Id, e.Role })
                .ToListAsync();

            var roleIds = users.Select(u => u.Role).Distinct().ToList();

            var roles = await _rolesCollection
                .Find(r => roleIds.Contains(r.Id))
                .ToListAsync();

            // Step 3: Build maps
            var empToRoleIdMap = users.ToDictionary(u => u.Id, u => u.Role);
            var roleIdToNameMap = roles.ToDictionary(r => r.Id, r => r.role);

            // Step 4: Prepare response
            var response = leaves.Select(l =>
            {
                string roleName = "Unknown";

                if (empToRoleIdMap.TryGetValue(l.UserId, out var roleId))
                {
                    if (roleIdToNameMap.TryGetValue(roleId, out var resolvedRole))
                        roleName = resolvedRole;
                }

                return new
                {
                    id = l.Id.ToString(),
                    role = roleName,
                    startDate = l.FromDate,
                    endDate = l.ToDate,
                    reason = l.Reason,
                    status = l.Status
                };
            });

            return Ok(response);
        }

        [HttpPost("escalate")]
        public async Task<IActionResult> EscalateRequest([FromBody] RequestParam requestParam)
        {
            var requester = HttpContext.GetCurrentEmployee();
            if (requester == null)
            {
                return Unauthorized(new { error = "Authentication required" });
            }

            _logger.LogInformation("{Action} called by UserId: {UserId} with payload: {@Payload}",
                nameof(EscalateRequest), requester.Id, requestParam);

            if (string.IsNullOrWhiteSpace(requestParam.LeaveId) || !ObjectId.TryParse(requestParam.LeaveId, out var leaveObjectId))
                return BadRequest(new { error = "Invalid LeaveId" });

            var leave = await _leaveCollection.Find(l => l.Id == leaveObjectId).FirstOrDefaultAsync();
            if (leave == null)
                return NotFound(new { error = "Leave request not found" });

            if (leave.IsEscalated)
                return BadRequest(new { error = "Leave request has already been escalated" });

            // Build escalation details
            var escalationDetails = new EscalationDetails
            {
                EscalatedDate = DateTime.UtcNow.ToIST(),
                EscalationStatus = "PENDING",
                ResolvedDate = default, // will be null in Mongo
                Comments = null
            };

            // Build ReqStatusTracking entry
            var trackingEntry = new RequestStatusTracking
            {
                Status = "ESCALATED",
                ProcessedBy = $"{requester.FirstName} {requester.LastName}".Trim(),
                ProcessedById = requester.Id,
                ProcessedAt = DateTime.UtcNow.ToIST(),
                Comment = $"{requester.FirstName} {requester.LastName} Escalated the {leave.Type} request"
            };

            // Update escalation status and details
            var update = Builders<Leave>.Update
                .Set(l => l.IsEscalated, true)
                .Set(l => l.UpdatedAt, DateTime.UtcNow.ToIST())
                .Set(l => l.EscalationDet, escalationDetails)
                 .Push(l => l.ReqStatusTracking, trackingEntry);

            await _leaveCollection.UpdateOneAsync(l => l.Id == leaveObjectId, update);

            // Notify reporting managers and HR/Admin
            var fcmTokens = await GetTargetFcmMessages(leave.UserId, leave.UserId, leave.Id.ToString());
            string typeLabel = leave.Type.Trim().ToUpperInvariant() switch
            {
                "LEAVE" => "Leave",
                "WFH" => "Work From Home",
                "EXTRA" => "Extra Working Day",
                "HALFDAY" => "Half Day Leave",
                _ => "Leave"
            };

            if (fcmTokens.Any())
            {
                await _fcmService.SendPushNotificationsAsync(
                    fcmTokens,
                    $"{typeLabel} Request Escalated",
                    $"The {typeLabel.ToLower()} request from {requester.FirstName} {requester.LastName} has been escalated for further review."
                );

                await _notificationService.AddNotificationAsync(new AnnouncementRequest
                {
                    Title = $"{typeLabel} Request Escalated",
                    Body = $"The {typeLabel.ToLower()} request from {requester.FirstName} {requester.LastName} has been escalated for further review.",
                    Screen = "Escalation",
                    Type = "NOTIFICATION",
                    SentBy = leave.UserId.ToString(),
                    UserId = leave.UserId.ToString(),
                    LeaveId = leaveObjectId.ToString()
                });
            }

            return Ok(new { success = true, message = "Request escalated successfully." });
        }

        //private async Task<List<string>> GetTargetFcmTokens(ObjectId applicantUserId, ObjectId processingEmpId, bool excludeSelf = true)
        //{
        //    var applicant = await _employeeCollection
        //        .Find(e => e.Id == applicantUserId)
        //        .FirstOrDefaultAsync();
        //    if (applicant == null) return new List<string>();

        //    var reportingToIds = (applicant.ReportingTo ?? new List<ObjectId>())
        //        .Where(id => id != applicantUserId)
        //        .ToList();

        //    // get role ObjectIds
        //    var wanted = new[] { "ADMIN", "HR", "SUPERADMIN" };
        //    var allowedRoleIds = await _rolesCollection
        //        .Find(r => wanted.Contains(r.role))
        //        .Project(r => r.Id)
        //        .ToListAsync();

        //    var reportingToFilter = reportingToIds.Count == 0
        //        ? Builders<Employee>.Filter.Where(e => false)
        //        : Builders<Employee>.Filter.In(e => e.Id, reportingToIds);

        //    var roleFilter = Builders<Employee>.Filter.In(e => e.Role, allowedRoleIds);

        //    var targetFilter = Builders<Employee>.Filter.Or(reportingToFilter, roleFilter);

        //    if (excludeSelf)
        //        targetFilter = Builders<Employee>.Filter.And(
        //            targetFilter,
        //            Builders<Employee>.Filter.Ne(e => e.Id, applicantUserId)
        //        );

        //    if (processingEmpId != applicantUserId)
        //    {
        //        targetFilter = Builders<Employee>.Filter.And(
        //            targetFilter,
        //            Builders<Employee>.Filter.Ne(e => e.Id, processingEmpId)
        //        );
        //    }

        //    var targetEmployees = await _employeeCollection
        //        .Find(targetFilter)
        //        .Project(e => new { e.Id, e.FcmToken })
        //        .ToListAsync();

        //    var tokens = targetEmployees
        //        .Select(x => x.FcmToken)
        //        .Where(t => !string.IsNullOrWhiteSpace(t))
        //        .Distinct()
        //        .ToList();

        //    if (!excludeSelf && !string.IsNullOrWhiteSpace(applicant.FcmToken))
        //        tokens.Add(applicant.FcmToken);

        //    return tokens.Distinct().ToList();
        //}
        private async Task<List<FcmMessageWithData>> GetTargetFcmMessages(
            ObjectId applicantUserId,
            ObjectId processingEmpId,
            string leaveId,
            bool excludeSelf = true)
        {
            var applicant = await _employeeCollection
                .Find(e => e.Id == applicantUserId)
                .FirstOrDefaultAsync();

            if (applicant == null)
                return new List<FcmMessageWithData>();

            var reportingToIds = (applicant.ReportingTo ?? new List<ObjectId>())
                .Where(id => id != applicantUserId)
                .ToList();

            // get role ObjectIds
            var wanted = new[] { "ADMIN", "HR", "SUPERADMIN" };
            var allowedRoleIds = await _rolesCollection
                .Find(r => wanted.Contains(r.role))
                .Project(r => r.Id)
                .ToListAsync();

            var reportingToFilter = reportingToIds.Count == 0
                ? Builders<Employee>.Filter.Where(e => false)
                : Builders<Employee>.Filter.In(e => e.Id, reportingToIds);

            var roleFilter = Builders<Employee>.Filter.In(e => e.Role, allowedRoleIds);

            var targetFilter = Builders<Employee>.Filter.Or(reportingToFilter, roleFilter);

            // always exclude applicant from this query; we’ll add them manually as EmployeeLeave
            targetFilter = Builders<Employee>.Filter.And(
                targetFilter,
                Builders<Employee>.Filter.Ne(e => e.Id, applicantUserId)
            );

            if (processingEmpId != applicantUserId)
            {
                targetFilter = Builders<Employee>.Filter.And(
                    targetFilter,
                    Builders<Employee>.Filter.Ne(e => e.Id, processingEmpId)
                );
            }

            var targetEmployees = await _employeeCollection
                .Find(targetFilter)
                .Project(e => new { e.Id, e.FcmToken })
                .ToListAsync();

            var messages = new List<FcmMessageWithData>();

            // Managers / HR / reportingTo → ManagerLeave
            foreach (var emp in targetEmployees)
            {
                if (string.IsNullOrWhiteSpace(emp.FcmToken))
                    continue;

                messages.Add(new FcmMessageWithData
                {
                    Token = emp.FcmToken,
                    Data = new Dictionary<string, string>
                    {
                        ["screen"] = "ManagerLeave",
                        ["leaveId"] = leaveId
                    }
                });
            }

            // Applicant self → EmployeeLeave (only if NOT excluded)
            if (!excludeSelf && !string.IsNullOrWhiteSpace(applicant.FcmToken))
            {
                var selfToken = applicant.FcmToken;

                if (!messages.Any(m => m.Token == selfToken))
                {
                    messages.Add(new FcmMessageWithData
                    {
                        Token = selfToken,
                        Data = new Dictionary<string, string>
                        {
                            ["screen"] = "EmployeeLeave",
                            ["leaveId"] = leaveId
                        }
                    });
                }
            }

            // Deduplicate by token, keep first payload (if any)
            messages = messages
                .GroupBy(m => m.Token)
                .Select(g => g.First())
                .ToList();

            return messages;
        }

        [HttpPost("getreportees")]
        public async Task<IActionResult> GetReportees([FromBody] RequestParam requestParam)
        {
            var employee = HttpContext.GetCurrentEmployee();
            if (employee == null)
            {
                return Unauthorized(new { error = "Authentication required" });
            }

            _logger.LogInformation("{Action} called by UserId: {UserId} with payload: {@Payload}",
                nameof(GetReportees), employee.Id, requestParam);

            var reportees = await _employeeCollection
                .Find(e => e.ReportingTo.Contains(employee.Id))
                .Project(e => new
                {
                    fName = e.FirstName,
                    lName = e.LastName,
                    ProfileImagePath = e.ProfileImagePath ?? string.Empty,
                    _id = e.Id.ToString()
                })
                .ToListAsync();

            return Ok(new { reportees });
        }

        [HttpGet("getCategories")]
        public async Task<IActionResult> GetLeaveCategories()
        {
            _logger.LogInformation("{Action} called", nameof(GetLeaveCategories));
            // Fetch all documents in LeaveCategory collection
            var leaveCategoryDocs = await _leaveCategoryCollection.Find(_ => true).ToListAsync();

            // Flatten all inner categories
            var categories = leaveCategoryDocs
                .SelectMany(doc => doc.Categories.Select(cat => new
                {
                    name = cat.Name,
                    description = cat.Description
                }))
                .ToList();

            return Ok(new { categories });
        }

        [HttpGet("getHolidays")]
        public async Task<IActionResult> GetHolidaysList()
        {
            _logger.LogInformation("{Action} called", nameof(GetHolidaysList));
            // Fetch all documents in LeaveCategory collection
            var holidayList = await _holidayCollection.Find(h => h.Year == DateTime.Now.Year.ToString()).FirstOrDefaultAsync();

            // Flatten all inner categories


            return Ok(new { holidayList });
        }

        private async Task SendLeaveEmailAsync(string leaveId)
        {
            if (string.IsNullOrWhiteSpace(leaveId))
                return;

            try
            {
                using var client = new HttpClient();

                var payload = new
                {
                    leaveId = leaveId
                };

                var content = new StringContent(
                    System.Text.Json.JsonSerializer.Serialize(payload),
                    Encoding.UTF8,
                    "application/json"
                );

                var response = await client.PostAsync(
                    "http://192.168.0.79:5678/webhook/leave-email",
                    content
         );

                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to trigger leave email for LeaveId {LeaveId}", leaveId);
                // Do NOT break leave flow for email failure
            }
        }
    }
}