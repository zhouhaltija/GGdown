using System.Data.Common;
using System.Diagnostics;
using GGdown.Data;
using GGdown.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit.Abstractions;

namespace GGdown.Tests.Services;

public sealed class UserPaginationTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData("last_download", true, new long[] { 2, 3, 1, 5, 7, 4, 6 })]
    [InlineData("last_download", false, new long[] { 1, 2, 3, 4, 6, 7, 5 })]
    [InlineData("download_count", true, new long[] { 1, 2, 3, 4, 5, 6, 7 })]
    [InlineData("download_count", false, new long[] { 3, 1, 2, 7, 6, 4, 5 })]
    [InlineData("added_at", true, new long[] { 3, 1, 2, 6, 4, 5, 7 })]
    [InlineData("added_at", false, new long[] { 1, 2, 3, 4, 5, 7, 6 })]
    public async Task Cursor_visits_pinned_duplicate_and_null_sort_values_once(string sortBy, bool descending, long[] expected)
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var service = new UserQueryService(fixture);
        var filter = new UserFilter(SortBy: sortBy, SortDesc: descending);
        UserCursor? cursor = null;
        var ids = new List<long>();
        for (var i = 0; i < 5; i++)
        {
            var page = await service.ListPageAsync("twitter", filter, cursor, pageSize: 2);
            Assert.InRange(page.Items.Count, 1, 2);
            ids.AddRange(page.Items.Select(u => u.Id));
            cursor = page.NextCursor;
            if (cursor is null) break;
        }
        Assert.Equal(expected, ids);
    }

    [Fact]
    public async Task Search_and_site_are_filtered_before_limit_and_cursor_cannot_cross_filters()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        await using (var db = await fixture.CreateAsync("twitter"))
        {
            db.Users.Add(new User { SiteId = "pixiv", RestId = "p1", ScreenName = "match", IsPinned = true });
            db.Users.Single(u => u.Id == 1).DisplayName = "match nickname";
            db.Users.Single(u => u.Id == 5).ScreenName = "match-screen";
            await db.SaveChangesAsync();
        }
        var service = new UserQueryService(fixture);
        var filter = new UserFilter("match");
        var first = await service.ListPageAsync("twitter", filter, pageSize: 1);
        Assert.Equal(1, Assert.Single(first.Items).Id);
        Assert.NotNull(first.NextCursor);
        var second = await service.ListPageAsync("twitter", filter, first.NextCursor, pageSize: 1);
        Assert.Equal(5, Assert.Single(second.Items).Id);
        Assert.Null(second.NextCursor);
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListPageAsync("pixiv", filter, first.NextCursor));
        await Assert.ThrowsAsync<ArgumentException>(() => service.ListPageAsync("twitter", new UserFilter(), first.NextCursor));
    }

    [Fact]
    public async Task Insertion_before_cursor_does_not_repeat_or_skip_existing_users()
    {
        using var fixture = new Fixture();
        await fixture.SeedAsync();
        var service = new UserQueryService(fixture);
        var first = await service.ListPageAsync("twitter", new UserFilter(), pageSize: 2);
        Assert.Equal(new long[] { 2, 3 }, first.Items.Select(u => u.Id));
        await using (var db = await fixture.CreateAsync("twitter"))
        {
            db.Users.Add(new User { SiteId = "twitter", RestId = "inserted", ScreenName = "inserted",
                IsPinned = true, LastDownloadAt = new DateTime(2030, 1, 1) });
            await db.SaveChangesAsync();
        }
        var seen = first.Items.Select(u => u.Id).ToList();
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var next = await service.ListPageAsync("twitter", new UserFilter(), cursor, pageSize: 2);
            seen.AddRange(next.Items.Select(u => u.Id));
            cursor = next.NextCursor;
        }
        Assert.Equal(new long[] { 2, 3, 1, 5, 7, 4, 6 }, seen);
    }

    [Fact]
    public async Task Large_sqlite_query_limits_materialized_rows_and_reports_actual_plan()
    {
        using var fixture = new Fixture();
        await using (var db = await fixture.CreateAsync("twitter"))
        {
            var stamp = new DateTime(2026, 1, 1);
            db.Users.AddRange(Enumerable.Range(1, 20_000).Select(i => new User
            {
                SiteId = "twitter", RestId = i.ToString(), ScreenName = $"user-{i}",
                IsPinned = i % 29 == 0, DownloadCount = i % 100, AddedAt = stamp.AddMinutes(i % 300),
                LastDownloadAt = i % 3 == 0 ? null : stamp.AddMinutes(i % 200),
            }));
            await db.SaveChangesAsync();
        }
        var service = new UserQueryService(fixture);
        var timer = Stopwatch.StartNew();
        var first = await service.ListPageAsync("twitter", new UserFilter());
        var next = await service.ListPageAsync("twitter", new UserFilter(), first.NextCursor);
        timer.Stop();
        Assert.Equal(200, first.Items.Count);
        Assert.Equal(200, next.Items.Count);
        Assert.Empty(first.Items.Select(u => u.Id).Intersect(next.Items.Select(u => u.Id)));
        var sql = fixture.Recorder.Sql!;
        Assert.Contains("LIMIT", sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("OFFSET", sql, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(fixture.Recorder.Parameters, p => Convert.ToString(p.Value) == "201");
        output.WriteLine($"SQLite 用户 20,000 行，首两页各200行，查询及物化耗时 {timer.Elapsed.TotalMilliseconds:F1}ms");
        output.WriteLine(sql);
        await using var command = fixture.Connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        foreach (var parameter in fixture.Recorder.Parameters)
            command.Parameters.AddWithValue(parameter.Name, parameter.Value ?? DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) output.WriteLine(reader.GetString(3));
    }

    private sealed class Fixture : ISiteDbContextFactory, IDisposable
    {
        public SqliteConnection Connection { get; } = new("Data Source=:memory:");
        public QueryRecorder Recorder { get; } = new();
        public Fixture()
        {
            Connection.Open();
            using var db = NewContext();
            db.Database.EnsureCreated();
        }
        private GGdownSiteDbContext NewContext() => new(new DbContextOptionsBuilder<GGdownSiteDbContext>()
            .UseSqlite(Connection).AddInterceptors(Recorder).Options);
        public Task<GGdownSiteDbContext> CreateAsync(string siteId, CancellationToken ct = default)
            => Task.FromResult(NewContext());
        public IReadOnlyList<string> ExistingSites() => ["twitter", "pixiv"];
        public void Dispose() => Connection.Dispose();
        public async Task SeedAsync()
        {
            var stamp = new DateTime(2026, 1, 2);
            await using var db = NewContext();
            db.Users.AddRange(Enumerable.Range(1, 7).Select(i => new User
            {
                Id = i, SiteId = "twitter", RestId = i.ToString(), ScreenName = $"user-{i}",
                IsPinned = i <= 3, DownloadCount = i == 7 ? 0 : i is 3 or 6 ? 1 : 2,
                LastDownloadAt = i is 1 or 4 or 6 ? null : i == 7 ? stamp.AddDays(-1) : stamp,
                AddedAt = i is 3 or 6 ? stamp.AddDays(1) : stamp,
            }));
            await db.SaveChangesAsync();
        }
    }

    private sealed class QueryRecorder : DbCommandInterceptor
    {
        public string? Sql { get; private set; }
        public List<(string Name, object? Value)> Parameters { get; private set; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
            {
                Sql = command.CommandText;
                Parameters = command.Parameters.Cast<DbParameter>().Select(p => (p.ParameterName, p.Value)).ToList();
            }
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }
}
