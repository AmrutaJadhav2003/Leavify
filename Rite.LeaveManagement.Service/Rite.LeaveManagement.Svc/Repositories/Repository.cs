using Rite.LeaveManagement.Svc.Interfaces;
using Microsoft.Extensions.Options;
using MongoDB.Driver;
using System.Collections.Generic;
using System.Threading.Tasks;
using Rite.LeaveManagement.Svc.Config;

namespace Rite.LeaveManagement.Svc.Repositories
{
    public class Repository<T> : IRepository<T>
    {
        private readonly IMongoCollection<T> _collection;

        public Repository(IOptions<MongoDbSettings> settings)
        {
            var client = new MongoClient(settings.Value.ConnectionString);
            var database = client.GetDatabase(settings.Value.DatabaseName);
            _collection = database.GetCollection<T>(typeof(T).Name);
        }

        public async Task<List<T>> GetAllAsync() => await _collection.Find(_ => true).ToListAsync();

        public async Task<T> GetByIdAsync(string id) =>
            await _collection.Find(Builders<T>.Filter.Eq("_id", id)).FirstOrDefaultAsync();

        public async Task AddAsync(T entity) => await _collection.InsertOneAsync(entity);

        public async Task UpdateAsync(string id, T entity) =>
            await _collection.ReplaceOneAsync(Builders<T>.Filter.Eq("_id", id), entity);

        public async Task DeleteAsync(string id) =>
            await _collection.DeleteOneAsync(Builders<T>.Filter.Eq("_id", id));
    }
}