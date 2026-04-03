using System;

namespace Rite.LeaveManagement.Svc.Models
{
    public class LeaveResponse
    {
        public string Id { get; set; }

        public string UserId { get; set; }

        public DateTime FromDate { get; set; }

        public DateTime ToDate { get; set; }

        public string Status { get; set; }

        public string Priority { get; set; }

        public string LeaveType { get; set; }

        public string? LeaveDescription { get; set; }

        public DateTime CreatedAt { get; set; }

        public byte[]? Document { get; set; }
    }
}
