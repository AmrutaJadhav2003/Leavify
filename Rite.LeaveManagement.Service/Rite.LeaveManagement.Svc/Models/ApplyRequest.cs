using Microsoft.AspNetCore.Http;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;

namespace Rite.LeaveManagement.Svc.Models
{
    public class ApplyRequest
    {
        public string requestedBy { get; set; }
        public string? LeaveId { get; set; }
        public string UserId { get; set; }
        public required string Type { get; set; }
        public required string SubType { get; set; }
        public DateTime FromDate { get; set; }
        public DateTime ToDate { get; set; }
        public string Reason { get; set; }
        public bool IsCompOff { get; set; }
        public bool IsHalfDay { get; set; }
        public List<string>? CompDates { get; set; }
        public List<LeaveDocument>? Documents { get; set; }

        //public string jwtToken { get; set; } 

        public string? Category { get; set; }
    }

    public class LeaveDocument
    {
        [BsonElement("docType")]
        public string DocType { get; set; }

        [BsonElement("docBytes")]
        public string DocBytes { get; set; }
    }


}
