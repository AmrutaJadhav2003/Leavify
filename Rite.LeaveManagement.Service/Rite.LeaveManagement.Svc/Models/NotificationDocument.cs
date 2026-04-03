using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Rite.LeaveManagement.Svc.Models
{
    public class NotificationDocument
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; }

        [BsonElement("title")]
        public string Title { get; set; }

        [BsonElement("body")]
        public string Body { get; set; }

        [BsonElement("screen")]
        public string Screen { get; set; }

        [BsonElement("type")]
        public string Type { get; set; }

        [BsonElement("persistTill")]
        public string PersistTill { get; set; } // Optional for ANNOUNCEMENT

        [BsonElement("sentBy")]
        [BsonRepresentation(BsonType.ObjectId)]
        public string SentBy { get; set; }

        [BsonElement("sentAt")]
        public DateTime SentAt { get; set; }

        [BsonElement("userId")]
        [BsonRepresentation(BsonType.ObjectId)]
        public string UserId { get; set; }

        [BsonElement("leaveId")]
        [BsonRepresentation(BsonType.ObjectId)]
        public string LeaveId { get; set; }
    }

}
