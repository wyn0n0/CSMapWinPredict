namespace CsDemoMap.Api.Services;

public sealed record MapPoint(double X, double Y);

public sealed record MapFeatureGeometry(
    string MapName,
    double PositionX,
    double PositionY,
    double Scale,
    MapPoint SiteA,
    MapPoint SiteB)
{
    public MapPoint Normalize(double worldX, double worldY) => new(
        (worldX - PositionX) / (Scale * 1024d),
        (PositionY - worldY) / (Scale * 1024d));
}

public static class MapFeatureGeometries
{
    private static readonly IReadOnlyDictionary<string, MapFeatureGeometry> Values =
        new Dictionary<string, MapFeatureGeometry>(StringComparer.OrdinalIgnoreCase)
        {
            ["de_mirage"] = new(
                "de_mirage",
                -3230,
                1713,
                5,
                new MapPoint(-378.01, -2102.56),
                new MapPoint(-1942.49, 356.14))
        };

    public static MapFeatureGeometry? Find(string mapName) =>
        Values.GetValueOrDefault(mapName);
}
