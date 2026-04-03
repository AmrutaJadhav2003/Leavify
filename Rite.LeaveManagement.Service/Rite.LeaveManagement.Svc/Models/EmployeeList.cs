using MongoDB.Bson;
using System.Collections.Generic;

namespace Rite.LeaveManagement.Svc.Models
{
    public class EmployeeDetails
    {
        //First
        //last
        //profile
        //mongoId
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string? ProfileImagePath { get; set; }
        public ObjectId MongoId { get; set; }
    }
}
