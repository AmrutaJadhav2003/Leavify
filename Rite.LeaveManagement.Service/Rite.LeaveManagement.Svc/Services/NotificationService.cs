using MongoDB.Bson;
using MongoDB.Driver;
using Rite.LeaveManagement.Svc.Models;

namespace Rite.LeaveManagement.Svc.Services
{
    public class NotificationService
    {
        private readonly IMongoCollection<BsonDocument> _notificationCollection;

        public NotificationService(IMongoDatabase database)
        {
            _notificationCollection = database.GetCollection<BsonDocument>("notifications");
        }

        public async Task AddNotificationAsync(AnnouncementRequest request)
        {
            try
            {
                if (
                        string.IsNullOrWhiteSpace(request.Body) ||
                        string.IsNullOrWhiteSpace(request.Type) ||
                        string.IsNullOrWhiteSpace(request.SentBy))
                {
                    throw new ArgumentException("Missing required fields.");
                }

                //if (request.Type.ToUpper() == "ANNOUNCEMENT" && request.PersistTill == null)
                //{
                //    throw new ArgumentException("PersistTill is required for ANNOUNCEMENT.");
                //}

                var doc = new BsonDocument
        {
            { "title", string.IsNullOrWhiteSpace(request.Title)?  BsonNull.Value:  request.Title},
            { "body", request.Body },
            { "screen", request.Type.ToUpper() == "ANNOUNCEMENT" ? "HOME" :request.Screen },
            { "type", request.Type.ToUpper() },
            { "sentBy", ObjectId.Parse(request.SentBy) },
            { "sentAt", DateTime.UtcNow },
            { "userId", string.IsNullOrWhiteSpace(request.UserId) ? BsonNull.Value : ObjectId.Parse(request.UserId) },
            { "leaveId", string.IsNullOrWhiteSpace(request.LeaveId) ? BsonNull.Value : ObjectId.Parse(request.LeaveId) }
        };

                await _notificationCollection.InsertOneAsync(doc);
            }
            catch (Exception ex)
            {

                throw;
            }
        }
    }

}
