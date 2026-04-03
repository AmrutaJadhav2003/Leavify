namespace Rite.LeaveManagement.Svc.Models
{
    public class RequestParam
    {
        //public required string JWTToken { get; set; }

        public string? UserId { get; set; }
        public string? LeaveId { get; set; }

        public string? Comment { get; set; }
    }
}
