using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;

namespace Rite.LeaveManagement.Svc.Models
{
    public class LeaveCategory
    {
        [BsonId]
        [BsonRepresentation(BsonType.ObjectId)]
        public ObjectId Id { get; set; }

        [BsonElement("Categories")]
        public List<CategoryItem> Categories { get; set; }
    }

    public class CategoryItem
    {
        [BsonElement("Name")]
        public string Name { get; set; }

        [BsonElement("Description")]
        public string Description { get; set; }
    }
}
