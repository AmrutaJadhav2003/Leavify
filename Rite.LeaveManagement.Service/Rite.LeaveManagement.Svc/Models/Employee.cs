using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Rite.LeaveManagement.Svc.Models
{
    public class Employee
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public ObjectId Id { get; set; }

        [BsonElement("fName")]
        public string FirstName { get; set; }

        [BsonElement("lName")]
        public string LastName { get; set; }

        [BsonElement("email")]
        public string Email { get; set; }

        [BsonElement("mobile")]
        public string Mobile { get; set; }

        [BsonElement("role")]
        [BsonRepresentation(BsonType.ObjectId)]
        public ObjectId Role { get; set; }

        [BsonElement("reportingTo")]
        [BsonRepresentation(BsonType.ObjectId)]
        public List<ObjectId> ReportingTo { get; set; }

        [BsonElement("projectList")]
        [BsonRepresentation(BsonType.ObjectId)]
        public List<ObjectId> ProjectList { get; set; }

        [BsonElement("isActive")]
        public bool IsActive { get; set; }

        [BsonElement("createdAt")]
        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime CreatedAt { get; set; }

        [BsonElement("balance")]
        [BsonRepresentation(BsonType.Decimal128)]
        public decimal Balance { get; set; }

        [BsonElement("approved")]
        public int Approved { get; set; }

        [BsonElement("rejected")]
        public int Rejected { get; set; }

        [BsonElement("pending")]
        public int Pending { get; set; }

        [BsonElement("organization")]
        [BsonRepresentation(BsonType.ObjectId)]
        public ObjectId Organization { get; set; }

        [BsonElement("jwtToken")]
        public string JwtToken { get; set; }

        [BsonElement("fcmToken")]
        public string FcmToken { get; set; }

        [BsonElement("password")]
        public string Password { get; set; }

        [BsonElement("empId")]
        public string EmpId { get; set; }

        [BsonElement("joiningDate")]
        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime JoiningDate { get; set; }

        public string? ProfileImagePath { get; set; }

        [BsonElement("canSendAnnouncement")]
        public bool? CanSendAnnouncement   {get;set;}

        [BsonElement("designation")]
        public string? Designation { get; set; }
    }
}

