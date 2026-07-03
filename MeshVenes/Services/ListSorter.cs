using System;
using System.Collections.Generic;
using System.Linq;

namespace MeshVenes.Services;

public static class ListSorter
{
    public static List<T> Sort<T>(
        IEnumerable<T> items,
        ListSortMode mode,
        bool descending,
        Func<T, DateTime> getLastHeardUtc,
        Func<T, int?> getHopsAway,
        Func<T, bool> isFavorite,
        Func<T, bool> isMyNode,
        Func<T, string?> getSortNameKey,
        Func<T, string?> getSortIdKey)
    {
        return mode switch
        {
            ListSortMode.LastHeard when descending => items
                .OrderByDescending(item => getLastHeardUtc(item) != DateTime.MinValue)
                .ThenByDescending(getLastHeardUtc)
                .ThenBy(getSortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(getSortIdKey, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ListSortMode.LastHeard => items
                .OrderByDescending(item => getLastHeardUtc(item) != DateTime.MinValue)
                .ThenBy(getLastHeardUtc)
                .ThenBy(getSortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(getSortIdKey, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ListSortMode.HopsAway when descending => items
                .OrderBy(item => getHopsAway(item).HasValue ? 0 : 1)
                .ThenByDescending(item => getHopsAway(item) ?? int.MinValue)
                .ThenByDescending(item => getLastHeardUtc(item) != DateTime.MinValue)
                .ThenByDescending(getLastHeardUtc)
                .ThenBy(getSortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(getSortIdKey, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ListSortMode.HopsAway => items
                .OrderBy(item => getHopsAway(item) ?? int.MaxValue)
                .ThenByDescending(item => getLastHeardUtc(item) != DateTime.MinValue)
                .ThenByDescending(getLastHeardUtc)
                .ThenBy(getSortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(getSortIdKey, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ListSortMode.FavoritesFirst when descending => items
                .OrderByDescending(isFavorite)
                .ThenByDescending(item => getLastHeardUtc(item) != DateTime.MinValue)
                .ThenByDescending(getLastHeardUtc)
                .ThenBy(getSortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(getSortIdKey, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ListSortMode.FavoritesFirst => items
                .OrderBy(item => isFavorite(item) ? 1 : 0)
                .ThenByDescending(item => getLastHeardUtc(item) != DateTime.MinValue)
                .ThenByDescending(getLastHeardUtc)
                .ThenBy(getSortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(getSortIdKey, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ListSortMode.MyNodes when descending => items
                .OrderByDescending(isMyNode)
                .ThenByDescending(item => getLastHeardUtc(item) != DateTime.MinValue)
                .ThenByDescending(getLastHeardUtc)
                .ThenBy(getSortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(getSortIdKey, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ListSortMode.MyNodes => items
                .OrderBy(item => isMyNode(item) ? 1 : 0)
                .ThenByDescending(item => getLastHeardUtc(item) != DateTime.MinValue)
                .ThenByDescending(getLastHeardUtc)
                .ThenBy(getSortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(getSortIdKey, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            ListSortMode.Alphabetical when descending => items
                .OrderByDescending(getSortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenByDescending(getSortIdKey, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            _ => items
                .OrderBy(getSortNameKey, StringComparer.OrdinalIgnoreCase)
                .ThenBy(getSortIdKey, StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }
}
