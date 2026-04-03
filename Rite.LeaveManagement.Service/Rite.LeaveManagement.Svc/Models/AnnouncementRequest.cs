using Newtonsoft.Json;

namespace Rite.LeaveManagement.Svc.Models
{
    public class AnnouncementRequest
    {
        [JsonProperty("title")]
        public  string? Title { get; set; }

        [JsonProperty("body")]
        public required string Body { get; set; }

        [JsonProperty("screen")]
        public  string? Screen { get; set; }

        [JsonProperty("type")]
        public required string Type { get; set; } // "NOTIFICATION" or "ANNOUNCEMENT"

        [JsonProperty("persistTill")]
        public string? PersistTill { get; set; } 

        [JsonProperty("userId")]
        public string? UserId { get; set; } // string or null

        [JsonProperty("leaveId")]
        public string? LeaveId { get; set; } // string or null

        [JsonProperty("sentBy")]
        public required string SentBy { get; set; } // Required

        [JsonProperty("fcmToken")]
        public string? FCMToken { get; set; }
    }

}
