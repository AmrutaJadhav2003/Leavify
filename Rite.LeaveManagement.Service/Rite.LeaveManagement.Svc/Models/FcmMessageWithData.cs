using System.Collections.Generic;

namespace Rite.LeaveManagement.Svc.Models
{
    public class FcmMessageWithData
    {
        public string Token { get; set; } = string.Empty;
        public Dictionary<string, string> Data { get; set; } = new();
    }
}
