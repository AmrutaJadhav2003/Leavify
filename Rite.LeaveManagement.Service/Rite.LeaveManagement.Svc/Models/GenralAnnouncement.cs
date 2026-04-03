using Newtonsoft.Json;

namespace Rite.LeaveManagement.Svc.Models
{
    public class GenralAnnouncement
    {

        [JsonProperty("title")]
        public string Title { get; set; }
        [JsonProperty("body")]
        public string Body { get; set; }

        [JsonProperty("fcmToken")]
        public string FcmToken { get; set; } // single token per request


    }
}
