using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace Waypointer
{
    /// <summary>
    /// Orders search results into a walking route that starts at the player: the nearest spot first, then
    /// always the nearest one not yet visited (nearest neighbour), then improved by 2-opt - reversing a stretch of
    /// the route whenever that makes it shorter - until no reversal helps or the time budget runs out. The route
    /// is open (it does not return to the start) and the start is fixed at the player. Distances are horizontal.
    ///
    /// Unity-free, so the tests compile it directly.
    /// </summary>
    internal static class RoutePlanner
    {
        /// <summary>
        /// Returns the visiting order as indices into xs/zs. At most maxStops indices are returned, cut from the
        /// front of the optimised route, so the nearest stretch is kept.
        /// </summary>
        public static int[] Plan(float startX, float startZ, float[] xs, float[] zs, int count, int maxStops, double budgetMs)
        {
            if (count <= 0 || maxStops <= 0) return new int[0];

            int[] order = NearestNeighbour(startX, startZ, xs, zs, count);
            if (count >= 3) TwoOpt(startX, startZ, xs, zs, order, budgetMs);

            if (order.Length <= maxStops) return order;
            int[] cut = new int[maxStops];
            Array.Copy(order, cut, maxStops);
            return cut;
        }

        /// <summary>Total horizontal length of the route from the start through every stop, in order.</summary>
        public static double Length(float startX, float startZ, float[] xs, float[] zs, int[] order)
        {
            double total = 0;
            float px = startX, pz = startZ;
            for (int i = 0; i < order.Length; i++)
            {
                total += Dist(px, pz, xs[order[i]], zs[order[i]]);
                px = xs[order[i]]; pz = zs[order[i]];
            }
            return total;
        }

        private static int[] NearestNeighbour(float startX, float startZ, float[] xs, float[] zs, int count)
        {
            int[] order = new int[count];
            bool[] used = new bool[count];
            float px = startX, pz = startZ;
            for (int step = 0; step < count; step++)
            {
                int best = -1;
                double bestSqr = double.MaxValue;
                for (int i = 0; i < count; i++)
                {
                    if (used[i]) continue;
                    double dx = xs[i] - px, dz = zs[i] - pz;
                    double sqr = dx * dx + dz * dz;
                    if (sqr < bestSqr) { bestSqr = sqr; best = i; }
                }
                used[best] = true;
                order[step] = best;
                px = xs[best]; pz = zs[best];
            }
            return order;
        }

        // The first stop stays the one nearest the player (a route starts at the closest spot); 2-opt
        // improves the rest. Reversing order[i..j] (i >= 1) replaces the edges (order[i-1], order[i]) and
        // (order[j], order[j+1]) with (order[i-1], order[j]) and (order[i], order[j+1]); for j at the end of an
        // open route the second edge does not exist.
        private static void TwoOpt(float startX, float startZ, float[] xs, float[] zs, int[] order, double budgetMs)
        {
            Stopwatch sw = Stopwatch.StartNew();
            int n = order.Length;
            bool improved = true;
            while (improved)
            {
                improved = false;
                for (int i = 1; i < n - 1; i++)
                {
                    float ax = xs[order[i - 1]];
                    float az = zs[order[i - 1]];
                    float bx = xs[order[i]], bz = zs[order[i]];
                    double ab = Dist(ax, az, bx, bz);
                    for (int j = i + 1; j < n; j++)
                    {
                        float cx = xs[order[j]], cz = zs[order[j]];
                        double before = ab, after = Dist(ax, az, cx, cz);
                        if (j + 1 < n)
                        {
                            float dx = xs[order[j + 1]], dz = zs[order[j + 1]];
                            before += Dist(cx, cz, dx, dz);
                            after += Dist(bx, bz, dx, dz);
                        }
                        if (after < before - 1e-6)
                        {
                            Array.Reverse(order, i, j - i + 1);
                            improved = true;
                            bx = xs[order[i]]; bz = zs[order[i]];
                            ab = Dist(ax, az, bx, bz);
                        }
                    }
                    if (sw.Elapsed.TotalMilliseconds > budgetMs) return;
                }
            }
        }

        private static double Dist(float ax, float az, float bx, float bz)
        {
            double dx = ax - bx, dz = az - bz;
            return Math.Sqrt(dx * dx + dz * dz);
        }
    }
}
