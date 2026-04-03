using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Rite.LeaveManagement.Svc.Models
{
    public class VersionInfo
    {
        public ObjectId Id { get; set; }

        [BsonElement("version")]
        public string Version { get; set; }

        [BsonElement("isMaintenanceMode")]
        public bool IsMaintenanceMode { get; set; }= false;
    }
}
