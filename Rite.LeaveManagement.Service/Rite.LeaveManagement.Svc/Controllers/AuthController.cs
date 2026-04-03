using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MMS.Encryption;
using MongoDB.Driver;
using Rite.LeaveManagement.Svc.Models;
using Rite.LeaveManagement.Svc.Services;
using Rite.LeaveManagement.Svc.Extensions;

namespace Rite.LeaveManagement.Svc.Controllers
{
    [ApiController]
    [Route("auth")]
    public class AuthController : ControllerBase
    {
        private readonly IMongoCollection<Employee> _employeeCollection;
        private readonly IMongoCollection<VersionInfo> _versionCollection;
        private readonly JwtTokenService _jwtTokenService;
        private readonly ILogger<AuthController> _logger;

        public AuthController(
            IOptions<Config.MongoDbSettings> mongoSettings,
            JwtTokenService jwtTokenService,
            ILogger<AuthController> logger)
        {
            _logger = logger;

            var client = new MongoClient(mongoSettings.Value.ConnectionString);
            var database = client.GetDatabase(mongoSettings.Value.DatabaseName);
            _employeeCollection = database.GetCollection<Employee>("employees");
            _versionCollection = database.GetCollection<VersionInfo>("versioninfo");
            _jwtTokenService = jwtTokenService;
        }

        [HttpPost("login")]
        public async Task<IActionResult> Login([FromBody] Models.LoginRequest request)
        {
            _logger.LogInformation(
                "Login called. UserName={UserName}, LoginType={LoginType}, FcmTokenProvided={HasFcmToken}",
                request?.UserName,
                request?.LoginType,
                string.IsNullOrWhiteSpace(request?.FCMToken) ? false : true);

            try
            {
                string decryptedPass = string.Empty;
                Employee? user = null;

                if (request?.LoginType.ToLower() == LoginTypeEnum.Email.ToString().ToLower())
                {
                    _logger.LogDebug("Login: attempting email login for {UserName}", request.UserName);
                    user = await _employeeCollection.Find(e => e.Email == request.UserName).FirstOrDefaultAsync();
                }
                else if (request?.LoginType.ToLower() == LoginTypeEnum.Mobile.ToString().ToLower())
                {
                    _logger.LogDebug("Login: attempting mobile login for {UserName}", request.UserName);
                    user = await _employeeCollection.Find(e => e.Mobile == request.UserName).FirstOrDefaultAsync();
                }
                else
                {
                    _logger.LogWarning(
                        "Login: unsupported LoginType '{LoginType}' for UserName {UserName}",
                        request?.LoginType,
                        request?.UserName);
                    return BadRequest(new { error = "Unsupported login type" });
                }

                if (user == null)
                {
                    _logger.LogWarning("Login failed: user not found for UserName {UserName}", request?.UserName);
                    return Unauthorized("Invalid login credentials");
                }

                NativeEncryption.DecryptText(user.Password, ref decryptedPass);

                if (request?.Password != decryptedPass)
                {
                    _logger.LogWarning("Login failed: invalid password for UserName {UserName}", request?.UserName);
                    return Unauthorized("Invalid login credentials");
                }

                var updates = new List<UpdateDefinition<Employee>>();

                if (user.FcmToken != request.FCMToken)
                {
                    _logger.LogInformation(
                        "Login: updating FCM token for UserId {UserId}. OldTokenNull={OldNull}, NewTokenNull={NewNull}",
                        user.Id,
                        string.IsNullOrWhiteSpace(user.FcmToken),
                        string.IsNullOrWhiteSpace(request.FCMToken));
                    updates.Add(Builders<Employee>.Update.Set(e => e.FcmToken, request.FCMToken));
                }

                string token = _jwtTokenService.GenerateToken(user);
                updates.Add(Builders<Employee>.Update.Set(e => e.JwtToken, token));

                var combinedUpdate = Builders<Employee>.Update.Combine(updates);
                await _employeeCollection.UpdateOneAsync(e => e.Id == user.Id, combinedUpdate);

                _logger.LogInformation(
                    "Login successful for UserId {UserId}. JwtIssued={JwtTokenLength} chars, FcmUpdated={FcmUpdated}",
                    user.Id,
                    token?.Length ?? 0,
                    user.FcmToken != request.FCMToken);

                return Ok(new
                {
                    success = true,
                    message = "Login successful.",
                    userId = user.Id.ToString(),
                    jwtToken = token
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Login: unhandled exception for UserName {UserName}, LoginType={LoginType}",
                    request?.UserName,
                    request?.LoginType);
                throw;
            }
        }

        [HttpPost("logout")]
        public async Task<IActionResult> Logout()
        {
            _logger.LogInformation("Logout called");

            try
            {
                // Get current employee from HttpContext (set by middleware)
                var currentEmployee = HttpContext.GetCurrentEmployee();

                if (currentEmployee == null)
                {
                    _logger.LogWarning("Logout: No authenticated user found");
                    return Unauthorized(new { error = "Authentication required" });
                }

                // Get JWT token from context (also set by middleware)
                var jwtToken = HttpContext.GetJwtToken();

                if (string.IsNullOrWhiteSpace(jwtToken))
                {
                    _logger.LogWarning("Logout: JWT token not found in context for UserId {UserId}", currentEmployee.Id);
                    return BadRequest(new { error = "JWT token not found" });
                }

                // Clear both tokens in one atomic update
                var update = Builders<Employee>.Update
                    .Set(e => e.JwtToken, "")
                    .Set(e => e.FcmToken, "");

                // Use the JWT token to find the correct record (not the employee ID, in case of multiple sessions)
                var filter = Builders<Employee>.Filter.Eq(e => e.JwtToken, jwtToken);
                var result = await _employeeCollection.UpdateOneAsync(filter, update);

                if (result.MatchedCount == 0)
                {
                    _logger.LogWarning(
                        "Logout: no employee matched for JWT token for UserId {UserId} (may already be logged out).",
                        currentEmployee.Id);
                    return NotFound("Token not found or already logged out.");
                }

                _logger.LogInformation("Logout successful for UserId {UserId}", currentEmployee.Id);

                return Ok(new
                {
                    success = true,
                    message = "Logged out successfully."
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Logout: unhandled exception while processing logout");
                throw;
            }
        }

        [HttpGet("versioninfo")]
        public async Task<IActionResult> GetVersionInfo()
        {
            var doc = await _versionCollection
                .Find(FilterDefinition<VersionInfo>.Empty)
                .FirstOrDefaultAsync();

            if (doc == null)
                return NotFound(new { error = "Version info not found." });

            return Ok(new { version = doc.Version, isMaintenanceMode = doc.IsMaintenanceMode });
        }
    }
}