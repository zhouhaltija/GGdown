using GGdown.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Tests.Data;

public class GGdownDbContextTests : IDisposable
{
    private readonly (SqliteConnection, GGdownDbContext) _t;
    private GGdownDbContext Db => _t.Item2;

    public GGdownDbContextTests() => _t = TestDb.Create();
    public void Dispose() => _t.Item1.Dispose();

    [Fact]
    public async Task Can_save_and_read_user()
    {
        Db.Users.Add(new User
        {
            SiteId = "twitter", RestId = "44196397", ScreenName = "elonmusk",
            Source = UserSource.Manual, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
        });
        await Db.SaveChangesAsync();

        var found = await Db.Users.SingleAsync(u => u.RestId == "44196397");
        Assert.Equal("elonmusk", found.ScreenName);
    }

    [Fact]
    public async Task SiteId_RestId_is_unique()
    {
        Db.Users.Add(NewUser("u1"));
        await Db.SaveChangesAsync();
        Db.Users.Add(NewUser("u1"));

        await Assert.ThrowsAsync<DbUpdateException>(() => Db.SaveChangesAsync());
    }

    private static User NewUser(string restId) => new()
    {
        SiteId = "twitter", RestId = restId, ScreenName = "u" + restId,
        Source = UserSource.Following, AddedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow,
    };

    [Fact]
    public async Task Initialize_applies_migration_without_error()
    {
        // 内存库上 Migrate 需要先 EnsureDeleted 以清空 EnsureCreated 的痕迹
        await Db.Database.EnsureDeletedAsync();
        var ex = await Record.ExceptionAsync(() => DbInitializer.InitializeAsync(Db));
        Assert.Null(ex);
    }
}
