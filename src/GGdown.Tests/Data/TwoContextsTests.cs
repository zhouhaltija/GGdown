using GGdown.Data;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace GGdown.Tests.Data;

public class TwoContextsTests
{
    private static (SqliteConnection Connection, GGdownGlobalDbContext Db) OpenGlobal()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var db = new GGdownGlobalDbContext(new DbContextOptionsBuilder<GGdownGlobalDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (conn, db);
    }

    private static (SqliteConnection Connection, GGdownSiteDbContext Db) OpenSite()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var db = new GGdownSiteDbContext(new DbContextOptionsBuilder<GGdownSiteDbContext>().UseSqlite(conn).Options);
        db.Database.EnsureCreated();
        return (conn, db);
    }

    [Fact]
    public void Global_context_has_only_Settings_table()
    {
        var (conn, _) = OpenGlobal();
        var tables = TableNames(conn);
        Assert.Contains("Settings", tables);
        Assert.DoesNotContain("Accounts", tables);
        Assert.DoesNotContain("SiteSettings", tables);
    }

    [Fact]
    public void Site_context_has_site_tables_and_setting_entry_composite_key()
    {
        var (conn, db) = OpenSite();
        var tables = TableNames(conn);
        Assert.Contains("Accounts", tables);
        Assert.Contains("Users", tables);
        Assert.Contains("Jobs", tables);
        Assert.Contains("Files", tables);
        Assert.Contains("FollowingCache", tables);
        Assert.Contains("SiteSettings", tables);
        Assert.DoesNotContain("Settings", tables);
        // 复合主键：同库同 key 不同站点可共存（防御将来多站点混用）
        db.SiteSettings.Add(new SiteSettingEntry { SiteId = "twitter", Key = "k", Value = "1" });
        db.SaveChanges();
    }

    [Fact]
    public void Legacy_context_ignores_SiteId_and_SiteSettingEntry()
    {
        var conn = new SqliteConnection("Data Source=:memory:");
        conn.Open();
        var options = new DbContextOptionsBuilder<GGdownDbContext>().UseSqlite(conn).Options;
        using var db = new GGdownDbContext(options);
        db.Database.EnsureCreated();
        // 旧库 schema 不变：Jobs 表没有 SiteId 列
        Assert.DoesNotContain("SiteId", Columns(conn, "Jobs"));
        Assert.DoesNotContain("SiteSettings", TableNames(conn));
        // FK 约束与现状一致：先有 Account 才能挂 Job
        db.Accounts.Add(new Account { SiteId = "twitter", CookiePath = "a.txt", AddedAt = DateTime.UtcNow });
        db.SaveChanges();
        db.Jobs.Add(new DownloadJob { AccountId = 1, TargetKind = TargetKind.UserMedia });
        db.SaveChanges(); // 不抛（SiteId 被忽略）
    }

    [Fact]
    public void Site_context_maps_SiteId_on_jobs_and_files()
    {
        var (conn, _) = OpenSite();
        Assert.Contains("SiteId", Columns(conn, "Jobs"));
        Assert.Contains("SiteId", Columns(conn, "Files"));
    }

    private static List<string> TableNames(SqliteConnection c) =>
        Query(c, "SELECT name FROM sqlite_master WHERE type='table'");
    private static List<string> Columns(SqliteConnection c, string table)
    {
        var list = new List<string>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(1)); // 第 2 列才是列名（第 1 列是 cid）
        return list;
    }
    private static List<string> Query(SqliteConnection c, string sql)
    {
        var list = new List<string>();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(r.GetString(0));
        return list;
    }
}
