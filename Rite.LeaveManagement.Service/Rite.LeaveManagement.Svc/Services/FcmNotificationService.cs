using FirebaseAdmin.Messaging;
using Rite.LeaveManagement.Svc.Models;
using System.Text.Json;

namespace Rite.LeaveManagement.Svc.Services
{
    public interface IFcmNotificationService
    {
        // Same payload for all tokens
        Task<FcmResultSummary> SendPushNotificationsAsync(
            List<string> fcmTokens,
            string title,
            string body,
            Dictionary<string, string>? data = null);

        // Per-token payload
        Task<FcmResultSummary> SendPushNotificationsAsync(
            List<FcmMessageWithData> messages,
            string title,
            string body);
    }

    public class FcmNotificationService : IFcmNotificationService
    {
        // Wrapper: single payload for all tokens
        public async Task<FcmResultSummary> SendPushNotificationsAsync(
            List<string> fcmTokens,
            string title,
            string body,
            Dictionary<string, string>? data = null)
        {
            var items = (fcmTokens ?? new List<string>())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Select(t => new FcmMessageWithData
                {
                    Token = t,
                    // clone so each has its own dictionary instance
                    Data = data != null
                        ? new Dictionary<string, string>(data)
                        : new Dictionary<string, string>()
                })
                .ToList();

            return await SendPushNotificationsAsync(items, title, body);
        }

        // Core: per-token payload
        public async Task<FcmResultSummary> SendPushNotificationsAsync(
            List<FcmMessageWithData> messages,
            string title,
            string body)
        {
            var result = new FcmResultSummary();

            if (messages == null || messages.Count == 0)
                return result;

            var validItems = messages
                .Where(m => m != null && !string.IsNullOrWhiteSpace(m.Token))
                .ToList();

            if (validItems.Count == 0)
                return result;

            var firebaseMessages = validItems.Select(m => new Message
            {
                Token = m.Token,
                Notification = new Notification
                {
                    Title = title,
                    Body = body
                },
                Data = m.Data ?? new Dictionary<string, string>(),

                // iOS / APNS config
                Apns = new ApnsConfig
                {
                    Headers = new Dictionary<string, string>
                    {
                        { "apns-push-type", "alert" },
                        { "apns-priority", "10" },
                        { "apns-topic", "com.ritetechnologies.leavify" } // bundle id
                    },
                    Aps = new Aps
                    {
                        Alert = new ApsAlert { Title = title, Body = body },
                        Sound = "default",
                        ContentAvailable = true,
                        MutableContent = true
                    }
                }
            }).ToList();

            try
            {
                var logPayload = firebaseMessages.Select(m => new
                {
                    m.Token,
                    Notification = m.Notification,
                    m.Data,
                    Apns = new
                    {
                        m.Apns.Headers,
                        Aps = new
                        {
                            m.Apns.Aps.Alert,
                            m.Apns.Aps.Sound,
                            m.Apns.Aps.ContentAvailable,
                            m.Apns.Aps.MutableContent
                        }
                    }
                });

                var json = JsonSerializer.Serialize(logPayload, new JsonSerializerOptions
                {
                    WriteIndented = true
                });
                var response = await FirebaseMessaging
                    .DefaultInstance
                    .SendEachAsync(firebaseMessages);

                result.Total = response.Responses.Count;
                result.SuccessCount = response.SuccessCount;
                result.FailureCount = response.FailureCount;

                for (int i = 0; i < response.Responses.Count; i++)
                {
                    var res = response.Responses[i];
                    var token = validItems[i].Token;

                    if (res.IsSuccess)
                        result.SuccessTokens.Add(token);
                    else
                        result.FailedTokens[token] =
                            res.Exception?.Message ?? "Unknown error";
                }
            }
            catch (Exception ex)
            {
                result.GlobalError = ex.Message;
            }

            return result;
        }
    }


    public class FcmResultSummary
    {
        public int Total { get; set; }
        public int SuccessCount { get; set; }
        public int FailureCount { get; set; }
        public List<string> SuccessTokens { get; set; } = new();
        public Dictionary<string, string> FailedTokens { get; set; } = new();
        public string? GlobalError { get; set; }
    }
}
