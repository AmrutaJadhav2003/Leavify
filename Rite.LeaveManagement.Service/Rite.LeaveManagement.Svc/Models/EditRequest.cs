namespace Rite.LeaveManagement.Svc.Models
{
    public class EditRequest
    {
        public required string LeaveId { get; set; }
        public required string UserId { get; set; }
        public DateTime? FromDate { get; set; }
        public DateTime? ToDate { get; set; }
        public string? Reason { get; set; }
        public bool? IsCompOff { get; set; }
        public bool? IsHalfDay { get; set; }
        public List<string>? CompDates { get; set; }
        public List<LeaveDocument>? Documents { get; set; }
        //public required string jwtToken { get; set; } // Added JWT token for authentication purposes
        public string? SubType { get; set; }

        public string? Category { get; set; }
    }
}
