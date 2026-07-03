using MeshVenes.Services;
using Xunit;

namespace MeshVenes.Tests;

public sealed class ListSorterTests
{
    [Fact]
    public void Sort_AlphabeticalAscending_UsesNameThenId()
    {
        var items = new[]
        {
            CreateItem("Zulu", "03"),
            CreateItem("Alpha", "02"),
            CreateItem("Alpha", "01")
        };

        var sorted = Sort(items, ListSortMode.Alphabetical, descending: false);

        Assert.Equal(["01", "02", "03"], sorted.Select(item => item.Id));
    }

    [Fact]
    public void Sort_LastHeardDescending_KeepsUnknownLast()
    {
        var old = CreateItem("Old", "01", lastHeardUtc: new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc));
        var recent = CreateItem("Recent", "02", lastHeardUtc: new DateTime(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc));
        var unknown = CreateItem("Unknown", "03");

        var sorted = Sort([old, unknown, recent], ListSortMode.LastHeard, descending: true);

        Assert.Equal(["02", "01", "03"], sorted.Select(item => item.Id));
    }

    [Fact]
    public void Sort_HopsAwayAscending_KeepsUnknownLast()
    {
        var items = new[]
        {
            CreateItem("Unknown", "03"),
            CreateItem("Two", "02", hopsAway: 2),
            CreateItem("One", "01", hopsAway: 1)
        };

        var sorted = Sort(items, ListSortMode.HopsAway, descending: false);

        Assert.Equal(["01", "02", "03"], sorted.Select(item => item.Id));
    }

    [Fact]
    public void Sort_FavoritesDescending_PutsFavoritesFirst()
    {
        var items = new[]
        {
            CreateItem("Regular", "01"),
            CreateItem("Favorite", "02", isFavorite: true)
        };

        var sorted = Sort(items, ListSortMode.FavoritesFirst, descending: true);

        Assert.Equal(["02", "01"], sorted.Select(item => item.Id));
    }

    [Fact]
    public void Sort_MyNodesDescending_PutsMyNodesFirst()
    {
        var items = new[]
        {
            CreateItem("Regular", "01"),
            CreateItem("Mine", "02", isMyNode: true)
        };

        var sorted = Sort(items, ListSortMode.MyNodes, descending: true);

        Assert.Equal(["02", "01"], sorted.Select(item => item.Id));
    }

    private static List<SortItem> Sort(IEnumerable<SortItem> items, ListSortMode mode, bool descending)
        => ListSorter.Sort(
            items,
            mode,
            descending,
            item => item.LastHeardUtc,
            item => item.HopsAway,
            item => item.IsFavorite,
            item => item.IsMyNode,
            item => item.Name,
            item => item.Id);

    private static SortItem CreateItem(
        string name,
        string id,
        DateTime? lastHeardUtc = null,
        int? hopsAway = null,
        bool isFavorite = false,
        bool isMyNode = false)
        => new(name, id, lastHeardUtc ?? DateTime.MinValue, hopsAway, isFavorite, isMyNode);

    private sealed record SortItem(
        string Name,
        string Id,
        DateTime LastHeardUtc,
        int? HopsAway,
        bool IsFavorite,
        bool IsMyNode);
}
