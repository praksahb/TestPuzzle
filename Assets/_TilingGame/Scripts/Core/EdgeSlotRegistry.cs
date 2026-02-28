using UnityEngine;
using System.Collections.Generic;

namespace TMKOC.Games.TilingGame
{
    /// <summary>
    /// Pure C# edge registry. No MonoBehaviour, no Unity physics.
    /// Tracks every edge in the cluster as either OPEN (available for snapping)
    /// or CLOSED (shared between two pieces).
    ///
    /// Design Doc Reference: Section 4 — Edge Slot Registry
    /// </summary>
    public class EdgeSlotRegistry
    {
        // ─────────────────────────────────────────────
        //  Data Structures (Section 4.2)
        // ─────────────────────────────────────────────

        /// <summary>
        /// A dictionary key that identifies a unique edge by its midpoint and length.
        /// Midpoint is rounded to 3 decimal places, length to 2 decimal places.
        /// Two EdgeKeys are considered equal if their midpoints are within 0.005 world units
        /// and their lengths are within 0.05 world units (Section 4.3).
        /// </summary>
        public struct EdgeKey : System.IEquatable<EdgeKey>
        {
            public Vector2 midpoint;
            public float length;

            public EdgeKey(Vector2 p1, Vector2 p2)
            {
                Vector2 mid = (p1 + p2) * 0.5f;
                midpoint = new Vector2(
                    Mathf.Round(mid.x * 1000f) / 1000f,
                    Mathf.Round(mid.y * 1000f) / 1000f
                );
                length = Mathf.Round(Vector2.Distance(p1, p2) * 100f) / 100f;
            }

            // Equality is defined by proximity, not exact float match
            public bool Equals(EdgeKey other)
            {
                return Vector2.Distance(midpoint, other.midpoint) < 0.005f
                    && Mathf.Abs(length - other.length) < 0.05f;
            }

            public override bool Equals(object obj)
            {
                return obj is EdgeKey other && Equals(other);
            }

            // Hash must be coarse enough that nearby keys land in the same bucket.
            // We quantize to a grid of 0.01 world units for hashing.
            public override int GetHashCode()
            {
                int hx = Mathf.RoundToInt(midpoint.x * 100f);
                int hy = Mathf.RoundToInt(midpoint.y * 100f);
                int hl = Mathf.RoundToInt(length * 20f);
                return (hx * 397) ^ (hy * 17) ^ hl;
            }
        }

        /// <summary>
        /// A single edge slot in the registry.
        /// Canonical world positions (p1, p2) are set ONCE at registration and never modified.
        /// </summary>
        public class EdgeSlot
        {
            public EdgeKey key;
            public Vector2 p1;            // Canonical world position, set once
            public Vector2 p2;            // Canonical world position, set once
            public bool isOpen;
            public Piece ownerPiece;      // The piece that first registered this edge
            public int edgeIndex;         // Which edge index on ownerPiece
            public Piece occupyingPiece;  // null if OPEN; set to incoming piece when CLOSED
        }

        // ─────────────────────────────────────────────
        //  Internal State
        // ─────────────────────────────────────────────

        private Dictionary<EdgeKey, EdgeSlot> slots = new Dictionary<EdgeKey, EdgeSlot>();

        /// <summary>
        /// Canonical vertex positions for every piece in the cluster.
        /// Written ONCE at snap time, never modified thereafter.
        /// All geometry queries read from here, NOT from piece.transform.
        /// This eliminates transform drift entirely (Section 2.3).
        /// </summary>
        public Dictionary<Piece, Vector2[]> canonicalVertices = new Dictionary<Piece, Vector2[]>();

        // ─────────────────────────────────────────────
        //  Public API (Section 4.4)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Called once at startup to register the starting cluster's edges.
        /// Edges shared between two initial pieces are marked CLOSED.
        /// Edges on the outer boundary are marked OPEN.
        /// </summary>
        public void BootstrapFromCluster(List<Piece> initialPieces)
        {
            slots.Clear();
            canonicalVertices.Clear();

            // First pass: store canonical vertices for every initial piece
            foreach (var piece in initialPieces)
            {
                Vector2[] verts = GetCurrentWorldVertices(piece);
                canonicalVertices[piece] = verts;
            }

            // Second pass: register all edges
            // If two pieces share the same edge (same midpoint + length), it's CLOSED.
            // Otherwise it's OPEN.
            foreach (var piece in initialPieces)
            {
                Vector2[] verts = canonicalVertices[piece];
                int edgeCount = verts.Length;

                for (int i = 0; i < edgeCount; i++)
                {
                    Vector2 p1 = verts[i];
                    Vector2 p2 = verts[(i + 1) % edgeCount];
                    EdgeKey key = new EdgeKey(p1, p2);

                    if (slots.TryGetValue(key, out EdgeSlot existing))
                    {
                        // This edge is shared between two pieces → CLOSE it
                        existing.isOpen = false;
                        existing.occupyingPiece = piece;
                    }
                    else
                    {
                        // First time seeing this edge → register as OPEN
                        slots[key] = new EdgeSlot
                        {
                            key = key,
                            p1 = p1,
                            p2 = p2,
                            isOpen = true,
                            ownerPiece = piece,
                            edgeIndex = i,
                            occupyingPiece = null
                        };
                    }
                }
            }

            Debug.Log($"[EdgeSlotRegistry] Bootstrap complete. {slots.Count} slots registered. " +
                      $"OPEN: {GetOpenSlots().Count}, CLOSED: {slots.Count - GetOpenSlots().Count}");
        }

        /// <summary>
        /// Registers all edges of a newly snapped piece.
        /// Edges that match existing OPEN slots are closed immediately (in Forgiving mode)
        /// or only the primary is closed (in Strict mode — handled by caller).
        /// Unmatched edges are added as new OPEN slots.
        /// </summary>
        public void RegisterPieceEdges(Piece piece, Vector2[] pieceCanonicalVerts)
        {
            canonicalVertices[piece] = pieceCanonicalVerts;

            int edgeCount = pieceCanonicalVerts.Length;
            for (int i = 0; i < edgeCount; i++)
            {
                Vector2 p1 = pieceCanonicalVerts[i];
                Vector2 p2 = pieceCanonicalVerts[(i + 1) % edgeCount];
                EdgeKey key = new EdgeKey(p1, p2);

                if (slots.TryGetValue(key, out EdgeSlot existing))
                {
                    // This edge matches an existing slot.
                    // If it was OPEN, close it (the new piece now occupies it).
                    if (existing.isOpen)
                    {
                        existing.isOpen = false;
                        existing.occupyingPiece = piece;
                    }
                    // If already CLOSED, something is very wrong (double-placement).
                    // Log but don't crash.
                    else
                    {
                        Debug.LogWarning($"[EdgeSlotRegistry] Edge already CLOSED at midpoint {key.midpoint}. " +
                                         $"Piece {piece.name} is overlapping {existing.occupyingPiece?.name}!");
                    }
                }
                else
                {
                    // New edge not in registry → add as OPEN (it's now part of the boundary)
                    slots[key] = new EdgeSlot
                    {
                        key = key,
                        p1 = p1,
                        p2 = p2,
                        isOpen = true,
                        ownerPiece = piece,
                        edgeIndex = i,
                        occupyingPiece = null
                    };
                }
            }
        }

        /// <summary>
        /// Explicitly marks a single slot as CLOSED.
        /// Used in Strict mode where only the primary matched edge is closed.
        /// </summary>
        public void CloseSlot(EdgeKey key, Piece occupyingPiece)
        {
            if (slots.TryGetValue(key, out EdgeSlot slot))
            {
                slot.isOpen = false;
                slot.occupyingPiece = occupyingPiece;
            }
        }

        /// <summary>
        /// Returns all currently OPEN slots (the cluster boundary).
        /// </summary>
        public List<EdgeSlot> GetOpenSlots()
        {
            List<EdgeSlot> open = new List<EdgeSlot>();
            foreach (var kvp in slots)
            {
                if (kvp.Value.isOpen)
                    open.Add(kvp.Value);
            }
            return open;
        }

        /// <summary>
        /// Scores all OPEN slots against a single dragged edge.
        /// Returns the best candidate within threshold, or null if none found.
        /// Score = midpoint distance + angular penalty (anti-parallel alignment).
        /// </summary>
        public EdgeSlot FindBestMatchingOpenSlot(GeometrySnapper.Edge draggedEdge, float threshold)
        {
            EdgeSlot best = null;
            float bestScore = threshold;

            foreach (var kvp in slots)
            {
                EdgeSlot slot = kvp.Value;
                if (!slot.isOpen) continue;

                // Length check
                if (Mathf.Abs(slot.key.length - draggedEdge.length) > 0.05f) continue;

                // Midpoint distance
                Vector2 slotMid = (slot.p1 + slot.p2) * 0.5f;
                Vector2 dragMid = (draggedEdge.p1 + draggedEdge.p2) * 0.5f;
                float midDist = Vector2.Distance(slotMid, dragMid);

                if (midDist > threshold) continue;

                // Angular penalty (anti-parallel: dragged edge should point opposite to slot edge)
                Vector2 dragDir = (draggedEdge.p2 - draggedEdge.p1).normalized;
                Vector2 slotDirReversed = (slot.p1 - slot.p2).normalized; // Anti-parallel
                float angleDiff = Mathf.Abs(Vector2.SignedAngle(dragDir, slotDirReversed));
                float angularPenalty = (angleDiff / 180f) * 2f;

                float score = midDist + angularPenalty;

                if (score < bestScore)
                {
                    bestScore = score;
                    best = slot;
                }
            }

            return best;
        }

        /// <summary>
        /// Given all projected edges of the incoming piece at its candidate pose,
        /// returns every OPEN slot that aligns with any of them.
        /// Used to build expectedNeighbours and sharedVertices before the geometry sweep.
        /// (Section 5.2)
        /// </summary>
        public List<EdgeSlot> FindAllMatchingOpenSlots(GeometrySnapper.Edge[] projectedEdges, float threshold)
        {
            List<EdgeSlot> matched = new List<EdgeSlot>();

            foreach (var kvp in slots)
            {
                EdgeSlot slot = kvp.Value;
                if (!slot.isOpen) continue;

                foreach (var projEdge in projectedEdges)
                {
                    // Length check
                    if (Mathf.Abs(slot.key.length - projEdge.length) > 0.05f) continue;

                    // Midpoint distance
                    Vector2 slotMid = (slot.p1 + slot.p2) * 0.5f;
                    Vector2 projMid = (projEdge.p1 + projEdge.p2) * 0.5f;
                    float midDist = Vector2.Distance(slotMid, projMid);

                    if (midDist > threshold) continue;

                    // Angular check (must be roughly anti-parallel)
                    Vector2 projDir = (projEdge.p2 - projEdge.p1).normalized;
                    Vector2 slotDirReversed = (slot.p1 - slot.p2).normalized;
                    float angleDiff = Mathf.Abs(Vector2.SignedAngle(projDir, slotDirReversed));

                    if (angleDiff < 30f) // Generous for "matching" detection
                    {
                        matched.Add(slot);
                        break; // This slot is matched, move to next slot
                    }
                }
            }

            return matched;
        }

        /// <summary>
        /// Returns the total number of registered slots (for debugging / UI).
        /// </summary>
        public int TotalSlotCount => slots.Count;

        // ─────────────────────────────────────────────
        //  Internal Helpers
        // ─────────────────────────────────────────────

        /// <summary>
        /// Reads the current world-space vertex positions from a piece's edgeSockets.
        /// Used ONLY during bootstrap (initial pieces) and for the currently-dragged piece.
        /// After a piece snaps, all reads come from canonicalVertices instead.
        /// </summary>
        public static Vector2[] GetCurrentWorldVertices(Piece piece)
        {
            if (piece.edgeSockets == null || piece.edgeSockets.Length < 3)
            {
                Debug.LogWarning($"[EdgeSlotRegistry] Piece {piece.name} has fewer than 3 edge sockets!");
                return new Vector2[0];
            }

            Vector2[] verts = new Vector2[piece.edgeSockets.Length];
            for (int i = 0; i < piece.edgeSockets.Length; i++)
            {
                if (piece.edgeSockets[i] != null)
                    verts[i] = piece.edgeSockets[i].position;
            }
            return verts;
        }
    }
}
