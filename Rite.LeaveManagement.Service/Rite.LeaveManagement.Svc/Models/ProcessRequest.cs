namespace Rite.LeaveManagement.Svc.Models
{
    public class ProcessRequest
    {
        public string LeaveId { get; set; }
        public string Status { get; set; }
        public string ActionTakenBy { get; set; }

        public string Comment { get; set; }

        //public string JwtToken { get; set; } 
    }
}
