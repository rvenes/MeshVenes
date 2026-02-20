using System.Text.Json.Serialization;

namespace MeshVenes.Models;

public sealed record GeoPoint(
    [property: JsonPropertyName("lat")] double Lat,
    [property: JsonPropertyName("lon")] double Lon);
