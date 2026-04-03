namespace Rite.LeaveManagement.Svc.Models
{
    public class ReporteeDto
    {
        public string UserId { get; set; } = default!;
        public string FirstName { get; set; } = default!;
        public string LastName { get; set; } = default!;
        public string ProfileImagePath { get; set; } = string.Empty;
        public string? Designation { get; set; }
    }
}
