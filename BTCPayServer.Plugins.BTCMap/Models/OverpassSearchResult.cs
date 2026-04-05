using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace BTCPayServer.Plugins.BTCMap.Models;

public class OverpassResponse
{
    [JsonPropertyName("elements")]
    public List<OverpassElement> Elements { get; set; } = new();
}

public class OverpassElement
{
    [JsonPropertyName("type")]
    public string Type { get; set; }

    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("lat")]
    public double? Lat { get; set; }

    [JsonPropertyName("lon")]
    public double? Lon { get; set; }

    [JsonPropertyName("center")]
    public OverpassCenter Center { get; set; }

    [JsonPropertyName("tags")]
    public Dictionary<string, string> Tags { get; set; } = new();

    [JsonIgnore]
    public double? EffectiveLat => Lat ?? Center?.Lat;

    [JsonIgnore]
    public double? EffectiveLon => Lon ?? Center?.Lon;
}

public class OverpassCenter
{
    [JsonPropertyName("lat")]
    public double Lat { get; set; }

    [JsonPropertyName("lon")]
    public double Lon { get; set; }
}
