namespace Rite.LeaveManagement.Svc.Models
{
    public class LoginRequest
    {
        public required string LoginType { get; set; }
        public required string UserName { get; set; }

        public required string Password { get; set; }

        public required string FCMToken { get; set; }
    }
}
