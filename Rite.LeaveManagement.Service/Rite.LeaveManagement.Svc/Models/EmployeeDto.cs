namespace Rite.LeaveManagement.Svc.Models
{
    public class EmployeeDto
    {
        public string Id { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string Email { get; set; }
        public string Mobile { get; set; }
        public string Role { get; set; }
        public List<string> ReportingTo { get; set; }
        public List<string> ProjectList { get; set; }
        public bool IsActive { get; set; }
        public DateTime CreatedAt { get; set; }
        public int Balance { get; set; }
        public int Approved { get; set; }
        public int Rejected { get; set; }
        public int Pending { get; set; }
        public string Organization { get; set; }
        public string EmpId { get; set; }
        public DateTime JoiningDate { get; set; }
    }
}
