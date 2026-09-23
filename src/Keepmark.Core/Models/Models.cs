using System.Text.Json.Serialization;

namespace Keepmark.Core.Models;

/// <summary>
/// A bookmark or folder node. Property names are camelCase on the wire to stay
/// byte-compatible with sync blobs written by the original JS client.
/// Folders are nodes with <see cref="Children"/> and no <see cref="Url"/>.
/// </summary>
public sealed class BookmarkNode
{
    [JsonPropertyName("id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Id { get; set; }

    [JsonPropertyName("title")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Title { get; set; }

    [JsonPropertyName("url")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Url { get; set; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Description { get; set; }

    [JsonPropertyName("tags")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Tags { get; set; }

    [JsonPropertyName("children")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<BookmarkNode>? Children { get; set; }

    [JsonIgnore]
    public bool IsFolder => Children is not null;

    [JsonIgnore]
    public bool IsSeparator => string.Equals(Url, XbsConstants.SeparatorUrl, StringComparison.Ordinal);
}

/// <summary>Locally stored sync credentials + state. The password field holds the derived key, never the raw password.</summary>
public sealed class SyncState
{
    public string Id { get; set; } = string.Empty;
    public string ServiceUrl { get; set; } = XbsConstants.DefaultServiceUrl;
    public string? Version { get; set; }
    public string? LastUpdated { get; set; }
}

/// <summary>Service status values mirror ApiServiceStatus in the original client.</summary>
public enum ServiceStatus
{
    Error = -1,
    Online = 1,
    Offline = 2,
    NoNewSyncs = 3
}

public sealed class ServiceInfo
{
    public string? Location { get; set; }
    public long MaxSyncSize { get; set; }
    public string? Message { get; set; }
    public ServiceStatus Status { get; set; } = ServiceStatus.Error;
    public string? Version { get; set; }
    public string? Url { get; set; }
}
