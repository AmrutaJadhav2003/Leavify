using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using Newtonsoft.Json;

namespace Rite.LeaveManagement.Svc.Models
{
    public class HolidayList
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        [JsonProperty("_id")]
        public string Id { get; set; }

        [BsonElement("year")]
        [JsonProperty("year")]
        public required string Year { get; set; }

        [BsonElement("holidayDates")]
        [JsonProperty("holidayDates")]
        public required List<HolidayDate> HolidayDates { get; set; }

        [BsonElement("organizationId")]
        [JsonProperty("organizationId")]
        public required string OrganizationId { get; set; }
    }

    public class HolidayDate
    {
        [BsonElement("date")]
        [JsonProperty("date")]
        public required string Date { get; set; } // ISO string format like "2025-01-01"

        [BsonElement("description")]
        [JsonProperty("description")]
        public required string Description { get; set; }

        [BsonElement("isOptional")]
        [JsonProperty("isOptional")]
        public bool IsOptional { get; set; }

        [BsonElement("groupCode")]
        [JsonProperty("groupCode")]
        public string? GroupCode { get; set; }

        [BsonElement("exchangedWith")]
        [JsonProperty("exchangedWith")]
        public string? ExchangedWith { get; set; } // Optional
    }
}
