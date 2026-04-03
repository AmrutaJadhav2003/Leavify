using Microsoft.AspNetCore.Mvc;

using Microsoft.Extensions.Options;

using MongoDB.Bson;

using MongoDB.Driver;

using Rite.LeaveManagement.Svc.Models;

namespace Rite.LeaveManagement.Svc.Controllers

{

    [ApiController]

    [Route("manager")]

    public class ManagerController : ControllerBase

    {

        private readonly IMongoCollection<Employee> _employeeCollection;

        private readonly IMongoCollection<Team> _teamCollection;

        private readonly IMongoCollection<Leave> _leaveCollection;

        private readonly IMongoCollection<Milestone> _milestoneCollection;

        public ManagerController(IOptions<Config.MongoDbSettings> mongoSettings)

        {

            var client = new MongoClient(mongoSettings.Value.ConnectionString);

            var db = client.GetDatabase(mongoSettings.Value.DatabaseName);

            _employeeCollection = db.GetCollection<Employee>("employees");

            _teamCollection = db.GetCollection<Team>("teams");

            _leaveCollection = db.GetCollection<Leave>("leaves");

            _milestoneCollection = db.GetCollection<Milestone>("milestones");

        }

        

    }

}
