using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Rite.LeaveManagement.Svc.Models
{
    public class Leave
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public ObjectId Id { get; set; }

        [BsonElement("requestedBy")]
        [BsonRepresentation(BsonType.ObjectId)]
        public ObjectId RequestedBy { get; set; }

        [BsonElement("userId")]
        [BsonRepresentation(BsonType.ObjectId)]
        public ObjectId UserId { get; set; }

        [BsonElement("type")]
        public string Type { get; set; } // Enum: LEAVE | WFH

        [BsonElement("subtype")]
        public string SubType { get; set; }

        [BsonElement("createdAt")]
        public DateTime CreatedAt { get; set; }

        [BsonElement("fromDate")]
        public DateTime FromDate { get; set; }

        [BsonElement("toDate")]
        public DateTime ToDate { get; set; }

        [BsonElement("reason")]
        public string Reason { get; set; }

        [BsonElement("documents")]
        public List<StoredLeaveDocument> Documents { get; set; }

        [BsonElement("isCompOff")]
        public bool IsCompOff { get; set; }

        [BsonElement("compDates")]
        public List<DateTime> CompDates { get; set; }

        [BsonElement("isHalfDay")]
        public bool IsHalfDay { get; set; }

        [BsonElement("status")]
        public string Status { get; set; } // Enum: PENDING | APPROVED | DENIED | CANCELLED

        [BsonElement("isEscalated")]
        public bool IsEscalated { get; set; }

        [BsonElement("reqStatusTracking")]
        public List<RequestStatusTracking> ReqStatusTracking { get; set; }

        [BsonElement("escalationDet")]
        public EscalationDetails EscalationDet { get; set; }

        [BsonElement("updatedAt")]
        public DateTime UpdatedAt { get; set; }

        [BsonElement("reminderDetails")]
        public ReminderDetails ReminderDetails { get; set; }

        [BsonElement("category")]
        public string? Category { get; set; }

        [BsonElement("FirstName")]
        public  string? FirstName { get; set; }

        [BsonElement("LastName")]
        public  string? LastName { get; set; }
    }

    public class StoredLeaveDocument
    {
        [BsonElement("docType")]
        public string? DocType { get; set; }

        [BsonElement("docPath")]
        public string? DocPath { get; set; }

        [BsonElement("docBytes")]
        public string? DocBytes { get; set; }
    }
    public class RequestStatusTracking
    {
        [BsonElement("status")]
        public string Status { get; set; } // Enum

        [BsonElement("processedBy")]
        public string ProcessedBy { get; set; }

        [BsonElement("processedAt")]
        public DateTime ProcessedAt { get; set; }

        [BsonElement("comment")]
        public string Comment { get; set; }

        [BsonElement("processedById")]
        [BsonRepresentation(BsonType.ObjectId)]
        public ObjectId ProcessedById { get; set; }

    }
    public class EscalationDetails
    {
        [BsonElement("escalatedDate")]
        public DateTime EscalatedDate { get; set; }

        [BsonElement("escalationStatus")]
        public string EscalationStatus { get; set; } // Enum: PENDING | RESOLVED

        [BsonElement("resolvedDate")]
        public DateTime ResolvedDate { get; set; }

        [BsonElement("comments")]
        public string Comments { get; set; }
    }
    public class ReminderDetails
    {
        [BsonElement("reminderSentAt")]
        public DateTime ReminderSentAt { get; set; }

        [BsonElement("reminderCount")]
        public int ReminderCount { get; set; }
    }


}

