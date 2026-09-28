using System;
using System.Collections.Generic;
using System.IO;
using Foot_Tracker.Models;

namespace Foot_Tracker.Services;

/// <summary>
/// §406. The one picture every map's box is drawn on: the whole world in
/// DataFiles/RegionMaps/World.png (4096×4096), in place of §399's picture
/// per region. One picture means one editor with no region to pick, boxes
/// for the maps that belong to no region, and a species' locations shown
/// on one canvas.
///
/// The boxes drawn on the four region pictures before this are carried
/// across rather than redrawn: each region picture turned out to be the
/// same artwork at 1.95× less, and the offset of each inside the world
/// picture was measured (a template match at 0.99 correlation for Kanto,
/// Johto and Hoenn, 0.96 for Sinnoh). A box whose picture size is one of
/// those four is moved by that offset and scale on the way in - from the
/// server, from the cache - and a republish writes it back in world
/// pixels; a box already in world pixels passes through untouched. The
/// four sizes are all different, so the size alone says which it was.
/// </summary>
public static class RegionMaps
{
    public const string WorldFile = "World.png";

    public const int WorldWidth = 4096;

    public const int WorldHeight = 4096;

    public static readonly string Folder =
        Path.Combine(AppContext.BaseDirectory, "DataFiles", "RegionMaps");

    public static string WorldPath => Path.Combine(Folder, WorldFile);

    /// <summary>Where a §399 region picture sits in the world picture:
    /// its size (the key), and the offset and scale that place it.</summary>
    private sealed record LegacyPicture(string Region, int Width, int Height, int OffsetX, int OffsetY, double Scale);

    private static readonly IReadOnlyList<LegacyPicture> Legacy = new[]
    {
        new LegacyPicture("Kanto", 754, 501, 1947, 1535, 1.95),
        new LegacyPicture("Johto", 747, 500, 1260, 1615, 1.95),
        new LegacyPicture("Hoenn", 750, 502, 607, 2029, 1.95),
        new LegacyPicture("Sinnoh", 758, 507, 2117, 27, 1.95),
    };

    /// <summary>Whether the box was drawn on one of the four region
    /// pictures rather than the world.</summary>
    public static bool IsLegacy(MapMarker marker) => Find(marker) is not null;

    /// <summary>The box in world pixels: moved by its region picture's
    /// offset and scale when it was drawn on one; itself otherwise.</summary>
    public static MapMarker ToWorld(MapMarker marker)
    {
        if (Find(marker) is not LegacyPicture picture)
            return marker;

        int x = (int)Math.Round(picture.OffsetX + marker.X * picture.Scale);
        int y = (int)Math.Round(picture.OffsetY + marker.Y * picture.Scale);
        int right = (int)Math.Round(picture.OffsetX + (marker.X + marker.Width) * picture.Scale);
        int bottom = (int)Math.Round(picture.OffsetY + (marker.Y + marker.Height) * picture.Scale);

        var moved = new MapMarker
        {
            X = Math.Clamp(x, 0, WorldWidth - 1),
            Y = Math.Clamp(y, 0, WorldHeight - 1),
            Width = Math.Max(1, Math.Min(right, WorldWidth) - Math.Clamp(x, 0, WorldWidth - 1)),
            Height = Math.Max(1, Math.Min(bottom, WorldHeight) - Math.Clamp(y, 0, WorldHeight - 1)),
            ImageWidth = WorldWidth,
            ImageHeight = WorldHeight,
        };

        return moved.IsValid ? moved : marker;
    }

    /// <summary>Every box of a page in world pixels, in place.</summary>
    public static void ToWorld(SpawnMap map)
    {
        for (int i = 0; i < map.Markers.Count; i++)
            map.Markers[i] = ToWorld(map.Markers[i]);
    }

    private static LegacyPicture? Find(MapMarker marker)
    {
        foreach (LegacyPicture picture in Legacy)
        {
            if (picture.Width == marker.ImageWidth && picture.Height == marker.ImageHeight)
                return picture;
        }

        return null;
    }
}
