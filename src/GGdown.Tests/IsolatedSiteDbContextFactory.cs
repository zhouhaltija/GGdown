using System.Collections.Concurrent;
using System.Data;
using GGdown.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Tests;

/// <summary>同一测试数据库使用独立连接，模拟应用中的前台查询与后台队列并发。</summary>
public sealed class IsolatedSiteDbContextFactory(string connectionString) : ISiteDbContextFactory
{
    private readonly ConcurrentBag<SqliteConnection> _connections = [];

    public bool HasOpenConnections => _connections.Any(c => c.State != ConnectionState.Closed);

    public Task<GGdownSiteDbContext> CreateAsync(string siteId, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var connection = new SqliteConnection(connectionString);
        var db = new GGdownSiteDbContext(new DbContextOptionsBuilder<GGdownSiteDbContext>()
            .UseSqlite(connection, contextOwnsConnection: true).Options);
        _connections.Add(connection);
        return Task.FromResult(db);
    }

    public IReadOnlyList<string> ExistingSites() => ["twitter", "pixiv", "douyin"];
}
