using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Rite.LeaveManagement.Svc.Models
{
    public class ManagementHierarchy
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; }

        [BsonElement("manager_id")]
        [BsonRepresentation(BsonType.ObjectId)]
        public string ManagerId { get; set; }

        [BsonElement("supervisor_id")]
        [BsonRepresentation(BsonType.ObjectId)]
        public string SupervisorId { get; set; }
    }
}
