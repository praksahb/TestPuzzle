using UnityEngine;
using System.Collections.Generic;

namespace TMKOC.Games.TilingGame
{
    /// <summary>
    /// Pure static utility class. No MonoBehaviour.
    /// Detects enclosed gaps in the cluster by analyzing the OPEN edge graph.
    /// 
    /// Design Doc Reference: Section 8 — Gap Detection
    /// 
    /// Concept: After every snap, the OPEN slots form the cluster boundary.
    /// A valid cluster has exactly ONE connected component (the outer loop).
    /// If an enclosed gap exists, the graph splits into 2+ components.
    /// </summary>
    public static class GapDetector
    {
        public struct GapScanResult
        {
            public bool hasGap;
            public float gapArea;
            public bool hasNearlyEnclosedGap;
        }

        /// <summary>
        /// Scans the registry's open edges for enclosed gaps.
        /// Returns whether a fully enclosed hole exists.
        /// </summary>
        /// <param name="registry">The edge slot registry to scan.</param>
        /// <param name="gapAreaThreshold">Minimum area (world units²) to count as a real gap.
        /// Recommended: 50% of a single kite's area.</param>
        public static GapScanResult Scan(EdgeSlotRegistry registry, float gapAreaThreshold)
        {
            List<EdgeSlotRegistry.EdgeSlot> openSlots = registry.GetOpenSlots();

            if (openSlots.Count < 3)
            {
                return new GapScanResult { hasGap = false };
            }

            // ─────────────────────────────────────────────
            //  Step 1: Build adjacency graph from open edges
            //  Nodes = unique endpoint positions (deduplicated by proximity)
            //  Edges = open slots connecting two nodes
            // ─────────────────────────────────────────────

            List<Vector2> uniqueNodes = new List<Vector2>();
            // Maps each open slot index to its two node indices
            int[] slotNodeA = new int[openSlots.Count];
            int[] slotNodeB = new int[openSlots.Count];

            for (int s = 0; s < openSlots.Count; s++)
            {
                slotNodeA[s] = FindOrAddNode(uniqueNodes, openSlots[s].p1);
                slotNodeB[s] = FindOrAddNode(uniqueNodes, openSlots[s].p2);
            }

            // Build adjacency list: nodeIndex → list of slot indices
            Dictionary<int, List<int>> adjacency = new Dictionary<int, List<int>>();
            for (int s = 0; s < openSlots.Count; s++)
            {
                int nA = slotNodeA[s];
                int nB = slotNodeB[s];

                if (!adjacency.ContainsKey(nA)) adjacency[nA] = new List<int>();
                if (!adjacency.ContainsKey(nB)) adjacency[nB] = new List<int>();

                adjacency[nA].Add(s);
                adjacency[nB].Add(s);
            }

            // ─────────────────────────────────────────────
            //  Step 2: Find connected components via BFS
            // ─────────────────────────────────────────────

            HashSet<int> visitedNodes = new HashSet<int>();
            List<List<int>> components = new List<List<int>>(); // Each component = list of slot indices

            foreach (var nodeIdx in adjacency.Keys)
            {
                if (visitedNodes.Contains(nodeIdx)) continue;

                // BFS from this node
                List<int> componentSlots = new List<int>();
                HashSet<int> componentSlotsSet = new HashSet<int>();
                Queue<int> queue = new Queue<int>();
                queue.Enqueue(nodeIdx);
                visitedNodes.Add(nodeIdx);

                while (queue.Count > 0)
                {
                    int current = queue.Dequeue();

                    if (!adjacency.ContainsKey(current)) continue;

                    foreach (int slotIdx in adjacency[current])
                    {
                        if (!componentSlotsSet.Contains(slotIdx))
                        {
                            componentSlotsSet.Add(slotIdx);
                            componentSlots.Add(slotIdx);
                        }

                        // Visit the other end of this slot
                        int otherNode = (slotNodeA[slotIdx] == current) ? slotNodeB[slotIdx] : slotNodeA[slotIdx];
                        if (!visitedNodes.Contains(otherNode))
                        {
                            visitedNodes.Add(otherNode);
                            queue.Enqueue(otherNode);
                        }
                    }
                }

                if (componentSlots.Count > 0)
                    components.Add(componentSlots);
            }

            // ─────────────────────────────────────────────
            //  Step 3: Analyze components
            //  1 component = healthy outer boundary
            //  2+ components = potential enclosed gap
            // ─────────────────────────────────────────────

            if (components.Count <= 1)
            {
                return new GapScanResult { hasGap = false };
            }

            // Find the largest component by area (that's the outer boundary)
            float largestArea = 0f;
            int largestIdx = 0;

            for (int c = 0; c < components.Count; c++)
            {
                float area = ComputeComponentArea(components[c], openSlots, uniqueNodes, slotNodeA, slotNodeB);
                if (area > largestArea)
                {
                    largestArea = area;
                    largestIdx = c;
                }
            }

            // Check all non-outer components for enclosed gaps
            GapScanResult result = new GapScanResult { hasGap = false };

            for (int c = 0; c < components.Count; c++)
            {
                if (c == largestIdx) continue; // Skip outer boundary

                float area = ComputeComponentArea(components[c], openSlots, uniqueNodes, slotNodeA, slotNodeB);

                // Check if this component is a closed loop (no dangling edges / degree-1 nodes)
                bool isClosed = IsClosedLoop(components[c], slotNodeA, slotNodeB, adjacency);

                if (area > gapAreaThreshold && isClosed)
                {
                    result.hasGap = true;
                    result.gapArea = area;
                }
                else if (area > gapAreaThreshold * 0.5f)
                {
                    // Nearly enclosed — warn but don't trigger game over
                    result.hasNearlyEnclosedGap = true;
                }
            }

            return result;
        }

        // ─────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Finds an existing node within deduplication threshold (0.005 world units)
        /// or adds a new one. Returns the index.
        /// </summary>
        private static int FindOrAddNode(List<Vector2> nodes, Vector2 point)
        {
            for (int i = 0; i < nodes.Count; i++)
            {
                if (Vector2.Distance(nodes[i], point) < 0.005f)
                    return i;
            }
            nodes.Add(point);
            return nodes.Count - 1;
        }

        /// <summary>
        /// Computes the approximate area enclosed by a component using the Shoelace formula.
        /// Reconstructs an ordered vertex list by walking edge adjacency.
        /// (Section 8.4)
        /// </summary>
        private static float ComputeComponentArea(List<int> slotIndices,
            List<EdgeSlotRegistry.EdgeSlot> allSlots,
            List<Vector2> uniqueNodes,
            int[] slotNodeA, int[] slotNodeB)
        {
            if (slotIndices.Count < 3) return 0f;

            // Collect all unique node indices in this component
            HashSet<int> nodeSet = new HashSet<int>();
            foreach (int si in slotIndices)
            {
                nodeSet.Add(slotNodeA[si]);
                nodeSet.Add(slotNodeB[si]);
            }

            List<Vector2> points = new List<Vector2>();
            foreach (int ni in nodeSet)
            {
                points.Add(uniqueNodes[ni]);
            }

            if (points.Count < 3) return 0f;

            // Sort by angle around centroid for Shoelace
            Vector2 centroid = Vector2.zero;
            foreach (var p in points) centroid += p;
            centroid /= points.Count;

            points.Sort((a, b) =>
            {
                float aAngle = Mathf.Atan2(a.y - centroid.y, a.x - centroid.x);
                float bAngle = Mathf.Atan2(b.y - centroid.y, b.x - centroid.x);
                return aAngle.CompareTo(bAngle);
            });

            // Shoelace formula
            float area = 0f;
            for (int i = 0; i < points.Count; i++)
            {
                Vector2 current = points[i];
                Vector2 next = points[(i + 1) % points.Count];
                area += (current.x * next.y) - (next.x * current.y);
            }
            return Mathf.Abs(area) * 0.5f;
        }

        /// <summary>
        /// A component is a "closed loop" if every node has degree >= 2
        /// (no dangling / dead-end edges).
        /// </summary>
        private static bool IsClosedLoop(List<int> slotIndices, int[] slotNodeA, int[] slotNodeB,
            Dictionary<int, List<int>> adjacency)
        {
            HashSet<int> componentNodes = new HashSet<int>();
            foreach (int si in slotIndices)
            {
                componentNodes.Add(slotNodeA[si]);
                componentNodes.Add(slotNodeB[si]);
            }

            foreach (int node in componentNodes)
            {
                if (!adjacency.ContainsKey(node)) return false;

                // Count how many of this node's edges are in THIS component
                int degree = 0;
                HashSet<int> slotSet = new HashSet<int>(slotIndices);
                foreach (int si in adjacency[node])
                {
                    if (slotSet.Contains(si)) degree++;
                }

                if (degree < 2) return false; // Dangling edge
            }

            return true;
        }
    }
}
