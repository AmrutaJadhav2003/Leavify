namespace Rite.LeaveManagement.Svc.Models
{
    public class ReminderRequest
    {
        public string LeaveId { get; set; }
        public string UserId { get; set; }

        //public string jwtToken { get; set; } // Added JWT token for authentication purposes
    }
}
