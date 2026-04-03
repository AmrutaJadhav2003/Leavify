using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using System;
using System.Collections.Generic;

namespace Rite.LeaveManagement.Svc.Models
{
    public class Team
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public string Id { get; set; }

        [BsonElement("team_name")]
        public string TeamName { get; set; }

        [BsonElement("team_manager")]
        public ObjectId TeamManager { get; set; }

        [BsonElement("team_members")]
        public List<ObjectId> TeamMembers { get; set; }

        [BsonElement("created_at")]
        [BsonDateTimeOptions(Kind = DateTimeKind.Utc)]
        public DateTime CreatedAt { get; set; }
    }
}

