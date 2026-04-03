using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Rite.LeaveManagement.Svc.Models
{
    public class Milestone
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; }

        [BsonElement("milestone_date")]
        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime MilestoneDate { get; set; }

        [BsonElement("description")]
        public string Description { get; set; }

        [BsonElement("company_id")]
        [BsonRepresentation(BsonType.ObjectId)]
        public string CompanyId { get; set; }

        [BsonElement("team_id")]
        [BsonRepresentation(BsonType.ObjectId)]
        public string TeamId { get; set; }
    }
}
