using System.Text.Json;

namespace GalleryGUI.Engine;

public static class JsonlParser
{
    public static EngineEvent? Parse(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        JsonElement json;
        try { json = JsonSerializer.Deserialize<JsonElement>(line); }
        catch (JsonException) { return null; }
        if (json.ValueKind != JsonValueKind.Object) return null;
        if (!json.TryGetProperty("ev", out var evEl) || evEl.ValueKind != JsonValueKind.String)
            return null;

        string? S(string name) => json.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String
            ? e.GetString() : null;
        long? L(string name) => json.TryGetProperty(name, out var e) &&
            (e.ValueKind == JsonValueKind.Number && e.TryGetInt64(out var v)) ? v : null;
        int? I(string name) => json.TryGetProperty(name, out var e) &&
            (e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out var v)) ? v : null;

        return new EngineEvent(
            Event: evEl.GetString()!,
            Url: S("url"), Path: S("path"), ItemId: S("item_id"), User: S("user"),
            Size: L("size"), Reason: S("reason"), Level: S("level"), Message: S("msg"), Kind: S("kind"),
            Total: L("total"), Skipped: L("skipped"), Failed: L("failed"),
            Protocol: I("protocol"), RunnerVersion: S("runner"), GalleryDlVersion: S("gallery_dl"),
            RestId: S("rest_id"), ScreenName: S("screen_name"),
            DisplayName: S("display_name"), AvatarUrl: S("avatar_url"),
            BannerUrl: S("banner_url"), Bio: S("bio"),
            FollowersCount: L("followers_count"), MediaCount: L("media_count"));
    }
}
