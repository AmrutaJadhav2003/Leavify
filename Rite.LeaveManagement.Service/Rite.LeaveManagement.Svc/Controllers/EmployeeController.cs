using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MongoDB.Bson;
using MongoDB.Driver;
using Rite.LeaveManagement.Svc.Models;
using Rite.LeaveManagement.Svc.Extensions;

namespace Rite.LeaveManagement.Svc.Controllers
{
    [ApiController]
    [Route("employee")]
    public class EmployeeController : ControllerBase
    {
        private readonly IMongoCollection<Leave> _leaves;
        private readonly IMongoCollection<Employee> _employees;
        private readonly IMongoCollection<Team> _teamsCollection;
        private readonly IMongoCollection<HolidayList> _holidayCollection;
        private readonly ILogger<EmployeeController> _logger;

        public EmployeeController(IOptions<Config.MongoDbSettings> mongoSettings,
                                  ILogger<EmployeeController> logger)
        {
            _logger = logger;

            var client = new MongoClient(mongoSettings.Value.ConnectionString);
            var database = client.GetDatabase(mongoSettings.Value.DatabaseName);
            _leaves = database.GetCollection<Leave>("leaves");
            _employees = database.GetCollection<Employee>("employees");
            _teamsCollection = database.GetCollection<Team>("teams");
            _holidayCollection = database.GetCollection<HolidayList>("holidaylist");
        }

        [HttpGet("summary/{userId}")]
        public async Task<IActionResult> GetEmployeeSummary(string userId)
        {
            // Get current employee from HttpContext (set by middleware)
            var currentEmployee = HttpContext.GetCurrentEmployee();
            if (currentEmployee == null)
            {
                _logger.LogWarning("GetEmployeeSummary: Authentication required for UserId {UserId}", userId);
                return Unauthorized(new { error = "Authentication required" });
            }

            _logger.LogInformation("GetEmployeeSummary called for UserId {UserId} by UserId {CurrentUserId}",
                userId, currentEmployee.Id);

            try
            {
                if (!ObjectId.TryParse(userId, out var currentUserId))
                {
                    _logger.LogWarning("GetEmployeeSummary: invalid user id {UserId}", userId);
                    return BadRequest("Invalid user ID");
                }

                var currentUser = await _employees.Find(e => e.Id == currentUserId).FirstOrDefaultAsync();
                if (currentUser == null)
                {
                    _logger.LogWarning("GetEmployeeSummary: user not found for UserId {UserId}", userId);
                    return NotFound("User not found");
                }

                var today = DateTime.UtcNow.Date;

                // Load referenced collections
                var db = _employees.Database;
                var roles = await db.GetCollection<BsonDocument>("roles").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
                var orgs = await db.GetCollection<BsonDocument>("organizations").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
                var projects = await db.GetCollection<BsonDocument>("projects").Find(FilterDefinition<BsonDocument>.Empty).ToListAsync();
                var allEmployees = await _employees.Find(_ => true).ToListAsync(); // used for reportingTo + elevated scope
                var mySubordinateIds = allEmployees
    .Where(e => e.ReportingTo != null && e.ReportingTo.Contains(currentUserId))
    .Select(e => e.Id)
    .ToList();

                _logger.LogDebug("GetEmployeeSummary: loaded {RoleCount} roles, {OrgCount} orgs, {ProjectCount} projects, {EmployeeCount} employees",
                    roles.Count, orgs.Count, projects.Count, allEmployees.Count);

                // Build lookups
                var roleMap = roles.ToDictionary(r => r["_id"].AsObjectId, r => r["role"].AsString);
                var orgMap = orgs.ToDictionary(o => o["_id"].AsObjectId, o => o["OrgName"].AsString);
                var projMap = projects.ToDictionary(p => p["_id"].AsObjectId, p => p["name"].AsString);
                var empMap = allEmployees.ToDictionary(e => e.Id, e => $"{e.FirstName} {e.LastName}");
                var empImageMap = allEmployees.ToDictionary(e => e.Id, e => e.ProfileImagePath ?? string.Empty);

                // Resolve names
                string resolvedRole = roleMap.TryGetValue(currentUser.Role, out var roleVal) ? roleVal : currentUser.Role.ToString();
                string resolvedOrg = orgMap.TryGetValue(currentUser.Organization, out var orgVal) ? orgVal : currentUser.Organization.ToString();
                var resolvedProjects = currentUser.ProjectList?.Select(p => projMap.TryGetValue(p, out var pname) ? pname : p.ToString()).ToList() ?? new();
                var resolvedManagers = currentUser.ReportingTo?.Select(m => empMap.TryGetValue(m, out var mname) ? mname : m.ToString()).ToList() ?? new();

                // Elevated roles can see ALL users
                var elevated = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "HR", "Admin", "SuperAdmin" };
                bool isElevated = elevated.Contains(resolvedRole);

                _logger.LogInformation("GetEmployeeSummary: resolved role {Role}, isElevated={IsElevated}", resolvedRole, isElevated);

                List<ObjectId> teammateIds;
                if (isElevated)
                {
                    teammateIds = allEmployees.Where(e => e.Id != currentUserId).Select(e => e.Id).ToList();
                }
                else
                {
                    var projectIds = currentUser.ProjectList ?? new List<ObjectId>();
                    var teammates = allEmployees.Where(e =>
                        e.Id != currentUserId &&
                        e.ProjectList != null &&
                        e.ProjectList.Any(p => projectIds.Contains(p))
                    ).ToList();

                    teammateIds = teammates.Select(e => e.Id).ToList();
                }

                _logger.LogInformation("GetEmployeeSummary: teammateIds count {Count} for UserId {UserId}", teammateIds.Count, userId);

                // Filters
                var approvedRegex = new BsonRegularExpression("^approved$", "i");
                var pendingRegex = new BsonRegularExpression("^pending$", "i");

                var teamUpcomingFilter = Builders<Leave>.Filter.And(
                    Builders<Leave>.Filter.In(l => l.UserId, teammateIds),
                    Builders<Leave>.Filter.Gte(l => l.ToDate, today),
                    Builders<Leave>.Filter.Regex(l => l.Status, approvedRegex)
                );

                var myUpcomingFilter = Builders<Leave>.Filter.And(
                    Builders<Leave>.Filter.Eq(l => l.UserId, currentUserId),
                    Builders<Leave>.Filter.Gte(l => l.ToDate, today),
                    Builders<Leave>.Filter.Regex(l => l.Status, approvedRegex)
                );

                var pendingMyApprovalFilter = Builders<Leave>.Filter.And(
    Builders<Leave>.Filter.In(l => l.UserId, mySubordinateIds),
    Builders<Leave>.Filter.Regex(l => l.Status, pendingRegex),

    // I have NOT already processed it
    Builders<Leave>.Filter.Not(
        Builders<Leave>.Filter.ElemMatch(l => l.ReqStatusTracking,
            t => t.ProcessedById == currentUserId
        )
    )
);

                var teamLeaves = await _leaves.Find(teamUpcomingFilter).ToListAsync();
                var myLeaves = await _leaves.Find(myUpcomingFilter).ToListAsync();
                var pendingTeamLeavesCount = teammateIds.Count == 0
                    ? 0
                    : (int)await _leaves.CountDocumentsAsync(pendingMyApprovalFilter);

                _logger.LogInformation(
                    "GetEmployeeSummary: MyUpcomingLeaves={MyCount}, TeamUpcomingLeaves={TeamCount}, PendingTeamLeaves={PendingTeamLeaves} for UserId {UserId}",
                    myLeaves.Count, teamLeaves.Count, pendingTeamLeavesCount, userId);

                var currentUserDto = new
                {
                    Id = currentUser.Id.ToString(),
                    FirstName = currentUser.FirstName,
                    LastName = currentUser.LastName,
                    Email = currentUser.Email,
                    Mobile = currentUser.Mobile,
                    Role = resolvedRole,
                    ReportingTo = resolvedManagers,
                    ProjectList = resolvedProjects,
                    IsActive = currentUser.IsActive,
                    CreatedAt = currentUser.CreatedAt.ToIST(),
                    Balance = currentUser.Balance,
                    Approved = currentUser.Approved,
                    Rejected = currentUser.Rejected,
                    Pending = currentUser.Pending,
                    Organization = resolvedOrg,
                    EmpId = currentUser.EmpId,
                    JoiningDate = currentUser.JoiningDate.ToIST(),
                    ProfileImageUrl = currentUser.ProfileImagePath,
                    CanSendAnnouncement = currentUser.CanSendAnnouncement,
                    Designation = currentUser.Designation
                };

                var teamLeaveDtos = teamLeaves
                    .OrderBy(l => l.FromDate)
                    .Select(l => new
                    {
                        UserId = l.UserId.ToString(),
                        EmployeeName = empMap.TryGetValue(l.UserId, out var name) ? name : "Unknown",
                        StartDate = l.FromDate.ToIST(),
                        EndDate = l.ToDate,
                        Reason = l.Reason,
                        Status = l.Status,
                        ProfileImageUrl = empImageMap.TryGetValue(l.UserId, out var img) ? img : string.Empty,
                        isHalfDay = l.IsHalfDay,
                        requestType = l.Type
                    }).ToList();

                var myLeaveDtos = myLeaves
                    .OrderBy(l => l.FromDate)
                    .Select(l => new
                    {
                        UserId = l.UserId.ToString(),
                        EmployeeName = $"{currentUser.FirstName} {currentUser.LastName}",
                        StartDate = l.FromDate.ToIST(),
                        EndDate = l.ToDate.ToIST(),
                        Reason = l.Reason,
                        Status = l.Status,
                        isHalfDay = l.IsHalfDay,
                        requestType = l.Type

                    }).ToList();

                _logger.LogInformation("GetEmployeeSummary: returning summary for UserId {UserId}", userId);

                return Ok(new
                {
                    CurrentUser = currentUserDto,
                    MyUpcomingLeaves = myLeaveDtos,
                    TeamUpcomingLeaves = teamLeaveDtos,
                    PendingLeavesFromTeam = pendingTeamLeavesCount
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "GetEmployeeSummary: unhandled exception for UserId {UserId}", userId);
                throw;
            }
        }

        [HttpPost("workingdays")]
        public async Task<IActionResult> GetWorkingDays([FromBody] RequestParam requestParam)
        {
            _logger.LogInformation("GetWorkingDays called for UserId {UserId}", requestParam.UserId);

            // Get current employee from HttpContext (set by middleware)
            var currentEmployee = HttpContext.GetCurrentEmployee();
            if (currentEmployee == null)
            {
                _logger.LogWarning("GetWorkingDays: Authentication required for UserId {UserId}", requestParam.UserId);
                return Unauthorized(new { error = "Authentication required" });
            }

            // 1. Validate userId
            if (!ObjectId.TryParse(requestParam.UserId, out var objectUserId))
            {
                _logger.LogWarning("GetWorkingDays: invalid userId {UserId}", requestParam.UserId);
                return BadRequest(new { error = "Invalid userId" });
            }

            // 2. Ensure employee exists
            var employee = await _employees
                .Find(e => e.Id == objectUserId)
                .FirstOrDefaultAsync();
            if (employee == null)
            {
                _logger.LogWarning("GetWorkingDays: employee not found for UserId {UserId}", requestParam.UserId);
                return NotFound(new { error = "Employee not found." });
            }

            // 3. Determine current month range (server's clock)
            var today = DateTime.UtcNow.Date;
            var monthStart = new DateTime(today.Year, today.Month, 1);
            var monthEnd = monthStart.AddDays(DateTime.DaysInMonth(today.Year, today.Month) - 1);

            _logger.LogDebug("GetWorkingDays: monthStart={MonthStart}, monthEnd={MonthEnd} for UserId {UserId}", monthStart, monthEnd, requestParam.UserId);

            // 4. Load holiday list for this year
            var holidayDoc = await _holidayCollection
                .Find(h => h.Year == monthStart.Year.ToString())
                .FirstOrDefaultAsync();
            var holidays = holidayDoc?
                .HolidayDates
                .Select(h => DateTime.Parse(h.Date).Date)
                .ToHashSet()
              ?? new HashSet<DateTime>();

            _logger.LogDebug("GetWorkingDays: loaded {HolidayCount} holidays for year {Year}", holidays.Count, monthStart.Year);

            // 5. Count total working days (Mon-Fri minus holidays)
            int totalWorkingDays = Enumerable
                .Range(0, (monthEnd - monthStart).Days + 1)
                .Select(d => monthStart.AddDays(d))
                .Count(dt =>
                    dt.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday
                    && !holidays.Contains(dt)
                );

            _logger.LogInformation("GetWorkingDays: totalWorkingDays={TotalWorkingDays} for {Year}-{Month} and UserId {UserId}",
                totalWorkingDays, monthStart.Year, monthStart.Month, requestParam.UserId);

            // 6. Fetch all APPROVED leaves overlapping this month
            var leaveFilter = Builders<Leave>.Filter.And(
                Builders<Leave>.Filter.Eq(l => l.UserId, objectUserId),
                Builders<Leave>.Filter.Eq(l => l.Status, "APPROVED"),
                Builders<Leave>.Filter.Lte(l => l.FromDate, monthEnd),
                Builders<Leave>.Filter.Gte(l => l.ToDate, monthStart)
            );
            var approvedLeaves = await _leaves
                .Find(leaveFilter)
                .ToListAsync();

            _logger.LogDebug("GetWorkingDays: found {ApprovedLeaveCount} approved leaves for UserId {UserId}",
                approvedLeaves.Count, requestParam.UserId);

            // 7. Sum up how many working days were taken as leave
            int leaveDays = approvedLeaves.Sum(l =>
            {
                var start = l.FromDate.Date < monthStart ? monthStart : l.FromDate.Date;
                var end = l.ToDate.Date > monthEnd ? monthEnd : l.ToDate.Date;
                return Enumerable
                    .Range(0, (end - start).Days + 1)
                    .Select(d => start.AddDays(d))
                    .Count(dt =>
                        dt.DayOfWeek is not DayOfWeek.Saturday and not DayOfWeek.Sunday
                        && !holidays.Contains(dt)
                    );
            });

            // 8. Compute remaining working days
            var remaining = totalWorkingDays - leaveDays;
            if (remaining < 0) remaining = 0;

            _logger.LogInformation("GetWorkingDays: leaveDays={LeaveDays}, remainingWorkingDays={Remaining}, balance={Balance} for UserId {UserId}",
                leaveDays, remaining, employee.Balance, requestParam.UserId);

            // 9. Return results
            return Ok(new
            {
                Balance = employee.Balance,
                RemainingWorkingDays = remaining
            });
        }

        [HttpPost("upload-profile")]
        [Consumes("multipart/form-data")]
        public async Task<IActionResult> UploadProfileImage([FromForm] ProfileUploadRequest request)
        {
            // Get current employee from HttpContext (set by middleware)
            var currentEmployee = HttpContext.GetCurrentEmployee();
            if (currentEmployee == null)
            {
                _logger.LogWarning("UploadProfileImage: Authentication required");
                return Unauthorized(new { error = "Authentication required" });
            }

            _logger.LogInformation(
                "UploadProfileImage called for UserId {UserId}. FileName={FileName}, FileSize={FileSize}",
                currentEmployee.Id,
                request.ProfileImage?.FileName,
                request.ProfileImage?.Length ?? 0);

            if (request.ProfileImage == null || request.ProfileImage.Length == 0)
            {
                _logger.LogWarning("UploadProfileImage: profile image not provided or empty");
                return BadRequest(new { error = "Profile image not provided." });
            }

            // Step 1: Get employee from database to ensure they exist
            var employee = await _employees.Find(e => e.Id == currentEmployee.Id).FirstOrDefaultAsync();
            if (employee == null)
            {
                _logger.LogWarning("UploadProfileImage: employee not found for UserId {UserId}", currentEmployee.Id);
                return NotFound(new { error = "Employee not found." });
            }

            var employeeId = employee.Id.ToString();
            var baseStoragePath = Environment.GetEnvironmentVariable("STORAGE_ROOT_PATH")
                      ?? "/data/leavify";
            // Step 2: Prepare storage path
            var storagePath = Path.Combine(baseStoragePath, "profilepics", employeeId);
            Directory.CreateDirectory(storagePath); // Ensure directory exists

            _logger.LogDebug("UploadProfileImage: storage path for EmployeeId {EmployeeId} is {StoragePath}", employeeId, storagePath);

            // Optional: delete previous image(s) if overwrite is allowed
            var existingFiles = Directory.GetFiles(storagePath);
            _logger.LogDebug("UploadProfileImage: deleting {Count} existing files for EmployeeId {EmployeeId}", existingFiles.Length, employeeId);

            foreach (var file in existingFiles)
                System.IO.File.Delete(file);

            // Step 3: Save new file
            var fileExt = Path.GetExtension(request.ProfileImage.FileName)?.Trim('.').ToLowerInvariant() ?? "png";
            var fileName = $"{Guid.NewGuid():N}.{fileExt}";
            var fullPath = Path.Combine(storagePath, fileName);

            using (var stream = new FileStream(fullPath, FileMode.Create))
            {
                await request.ProfileImage.CopyToAsync(stream);
            }

            // Step 4: Save relative path to employee record (for future retrieval)
            var relativePath = Path.Combine("profilepics", employeeId, fileName).Replace("\\", "/");
            var update = Builders<Employee>.Update.Set(e => e.ProfileImagePath, relativePath); // Save raw path; URL will be returned from another API
            await _employees.UpdateOneAsync(e => e.Id == employee.Id, update);

            _logger.LogInformation("UploadProfileImage: profile image updated for EmployeeId {EmployeeId}, RelativePath={RelativePath}", employeeId, relativePath);

            return Ok(new { success = true });
        }

        [HttpGet("reportees/{userId}")]
        public async Task<IActionResult> GetReportees(string userId)
        {
            _logger.LogInformation("GetReportees called for manager UserId {UserId}", userId);

            // Get current employee from HttpContext (set by middleware)
            var currentEmployee = HttpContext.GetCurrentEmployee();
            if (currentEmployee == null)
            {
                _logger.LogWarning("GetReportees: Authentication required for UserId {UserId}", userId);
                return Unauthorized(new { error = "Authentication required" });
            }

            if (!ObjectId.TryParse(userId, out var managerId))
                return BadRequest("Invalid user ID");

            // Load current user (also useful if you want to validate stored jwtToken)
            var currentUser = await _employees.Find(e => e.Id == managerId).FirstOrDefaultAsync();
            if (currentUser == null)
                return NotFound("User not found");

            // Find all employees who report to this manager
            var filter = Builders<Employee>.Filter.AnyEq(e => e.ReportingTo, managerId);

            var reportees = await _employees
                .Find(filter)
                .Project(e => new ReporteeDto
                {
                    UserId = e.Id.ToString(),
                    FirstName = e.FirstName,
                    LastName = e.LastName,
                    ProfileImagePath = e.ProfileImagePath ?? string.Empty,
                    Designation = e.Designation
                })
                .ToListAsync();

            return Ok(reportees);
        }
    }
}