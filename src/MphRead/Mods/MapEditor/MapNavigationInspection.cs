using System;
using System.Collections.Generic;
using System.Linq;
using MphRead.Mods.MapGen;

namespace MphRead.Mods.MapEditor;

public static class MapNavigationInspection
{
    public static int[] Find(MapNodePacker.NavigationGraph graph, int start, int destination)
    {
        if ((uint)start >= graph.Positions.Length || (uint)destination >= graph.Positions.Length) throw new ArgumentOutOfRangeException();
        var previous = Enumerable.Repeat(-1, graph.Positions.Length).ToArray();
        var distances = Enumerable.Repeat(float.PositiveInfinity, graph.Positions.Length).ToArray();
        var queue = new PriorityQueue<int, float>(); distances[start] = 0; queue.Enqueue(start, 0);
        while (queue.TryDequeue(out int current, out float distance))
        {
            if (distance > distances[current]) continue;
            if (current == destination) break;
            foreach (int next in graph.Neighbours[current])
            {
                float candidate = distance + (graph.Positions[next] - graph.Positions[current]).Length;
                if (candidate >= distances[next]) continue;
                distances[next] = candidate; previous[next] = current; queue.Enqueue(next, candidate);
            }
        }
        if (float.IsPositiveInfinity(distances[destination])) return System.Array.Empty<int>();
        var path = new List<int>(); for (int node = destination; node != -1; node = previous[node]) path.Add(node);
        path.Reverse(); return path.ToArray();
    }
}
