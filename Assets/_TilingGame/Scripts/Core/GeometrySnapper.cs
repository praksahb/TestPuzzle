using UnityEngine;
using System.Collections.Generic;

namespace TMKOC.Games.TilingGame
{
    /// <summary>
    /// Geometry utility for the snap pipeline.
    /// Now works WITH the EdgeSlotRegistry instead of independently scanning edges.
    ///
    /// Design Doc Reference: Sections 5, 6, 7.1 (ComputeBestSnapRotation)
    ///
    /// CHANGES from v1:
    /// - TrySnap() now queries the EdgeSlotRegistry as primary filter
    /// - Broken vertex loop fixed (vertices fully built before sweep)
    /// - ComputeExpectedNeighbours() excludes flush neighbours from geometry sweep
    /// - ConfirmNoBodyIntrusion() reads CanonicalVertexStore, not transforms
    /// - ComputeBestSnapRotation() added for Forgiving mode auto-rotate
    /// </summary>
    public static class GeometrySnapper
    {
        public struct Edge
        {
            public Vector2 p1;
            public Vector2 p2;
            public float length;
            public int index;
        }

        // ─────────────────────────────────────────────
        //  Edge Extraction
        // ─────────────────────────────────────────────

        /// <summary>
        /// Gets world-space edges from a piece's edgeSockets (live transform).
        /// Used ONLY for the currently-dragged piece.
        /// </summary>
        public static Edge[] GetWorldEdges(Piece piece)
        {
            if (piece.edgeSockets == null || piece.edgeSockets.Length < 3)
            {
                Debug.LogWarning($"Piece {piece.name} has fewer than 3 Edge Sockets assigned!");
                return new Edge[0];
            }

            int count = piece.edgeSockets.Length;
            Edge[] edges = new Edge[count];

            for (int i = 0; i < count; i++)
            {
                Transform t1 = piece.edgeSockets[i];
                Transform t2 = piece.edgeSockets[(i + 1) % count];

                if (t1 == null || t2 == null) continue;

                Vector2 wp1 = t1.position;
                Vector2 wp2 = t2.position;

                edges[i] = new Edge
                {
                    p1 = wp1,
                    p2 = wp2,
                    length = Vector2.Distance(wp1, wp2),
                    index = i
                };
            }
            return edges;
        }

        /// <summary>
        /// Gets edges from canonical (frozen) vertex positions.
        /// Used for all pieces already in the cluster. Eliminates transform drift.
        /// </summary>
        public static Edge[] GetCanonicalEdges(Vector2[] canonicalVerts)
        {
            if (canonicalVerts == null || canonicalVerts.Length < 3)
                return new Edge[0];

            int count = canonicalVerts.Length;
            Edge[] edges = new Edge[count];

            for (int i = 0; i < count; i++)
            {
                Vector2 p1 = canonicalVerts[i];
                Vector2 p2 = canonicalVerts[(i + 1) % count];

                edges[i] = new Edge
                {
                    p1 = p1,
                    p2 = p2,
                    length = Vector2.Distance(p1, p2),
                    index = i
                };
            }
            return edges;
        }

        // ─────────────────────────────────────────────
        //  Main Snap Pipeline (Design Doc Section 10.1 / 10.2)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Attempts to snap a dragged piece into the cluster using the Edge Slot Registry.
        /// Returns a SnapResult with success/failure and the target pose.
        /// </summary>
        public static SnapResult TrySnap(Piece dragged, EdgeSlotRegistry registry, SnapConfig config)
        {
            SnapResult result = new SnapResult
            {
                success = false,
                targetPos = dragged.transform.position,
                targetRot = dragged.transform.rotation,
                closedSlots = new List<EdgeSlotRegistry.EdgeSlot>(),
                reason = RejectionReason.NoOpenSlotNearby
            };

            Edge[] draggedEdges = GetWorldEdges(dragged);
            if (draggedEdges.Length == 0) return result;

            // ── Phase 1: Find the best matching open slot ──
            // Try each edge of the dragged piece against all OPEN slots
            EdgeSlotRegistry.EdgeSlot primarySlot = null;
            Edge bestDraggedEdge = default;
            float bestScore = config.snapDistanceThreshold;

            foreach (var dEdge in draggedEdges)
            {
                var candidate = registry.FindBestMatchingOpenSlot(dEdge, config.snapDistanceThreshold);
                if (candidate != null)
                {
                    // Compute score for this candidate
                    Vector2 dragMid = (dEdge.p1 + dEdge.p2) * 0.5f;
                    Vector2 slotMid = (candidate.p1 + candidate.p2) * 0.5f;
                    float score = Vector2.Distance(dragMid, slotMid);

                    if (score < bestScore)
                    {
                        bestScore = score;
                        primarySlot = candidate;
                        bestDraggedEdge = dEdge;
                    }
                }
            }

            if (primarySlot == null)
            {
                result.reason = RejectionReason.NoOpenSlotNearby;
                return result;
            }

            // ── Phase 2: Compute candidate pose ──
            Vector2 potentialPos;
            Quaternion potentialRot;
            ComputeCandidatePose(dragged, bestDraggedEdge, primarySlot, out potentialPos, out potentialRot);

            // ── Phase 2b: Angular tolerance check (Strict mode) ──
            if (!config.autoRotate)
            {
                Vector2 dragDir = (bestDraggedEdge.p2 - bestDraggedEdge.p1).normalized;
                Vector2 slotDirReversed = (primarySlot.p1 - primarySlot.p2).normalized;
                float angleDiff = Mathf.Abs(Vector2.SignedAngle(dragDir, slotDirReversed));

                if (angleDiff > config.angularToleranceDegrees)
                {
                    result.reason = RejectionReason.AngularMisalignment;
                    return result;
                }
            }

            // ── Phase 3: Project all edges at candidate pose ──
            Edge[] projectedEdges = ProjectEdgesAtPose(dragged, draggedEdges, potentialPos, potentialRot);

            // ── Phase 4: Find ALL matching open slots (for neighbour detection) ──
            List<EdgeSlotRegistry.EdgeSlot> matchedSlots =
                registry.FindAllMatchingOpenSlots(projectedEdges, config.snapDistanceThreshold);

            // Build expected neighbours set
            HashSet<Piece> expectedNeighbours = new HashSet<Piece>();
            HashSet<Vector2> sharedVertices = new HashSet<Vector2>();

            foreach (var slot in matchedSlots)
            {
                expectedNeighbours.Add(slot.ownerPiece);
                // Add slot endpoints as shared vertices
                AddRoundedVertex(sharedVertices, slot.p1);
                AddRoundedVertex(sharedVertices, slot.p2);
            }

            // Also add our own projected endpoints at matched edges
            foreach (var pEdge in projectedEdges)
            {
                AddRoundedVertex(sharedVertices, pEdge.p1);
                AddRoundedVertex(sharedVertices, pEdge.p2);
            }

            // ── Phase 5: Geometry confirmation (Section 6) ──
            Vector2[] projectedVerts = ProjectVerticesAtPose(dragged, draggedEdges, potentialPos, potentialRot);

            if (!ConfirmNoBodyIntrusion(projectedVerts, dragged, registry, expectedNeighbours, sharedVertices))
            {
                result.reason = RejectionReason.BodyOverlap;
                return result;
            }

            // ── SUCCESS ──
            result.success = true;
            result.targetPos = potentialPos;
            result.targetRot = potentialRot;
            result.closedSlots = matchedSlots;

            return result;
        }

        // ─────────────────────────────────────────────
        //  Candidate Pose Computation
        // ─────────────────────────────────────────────

        /// <summary>
        /// Computes where the piece needs to be positioned and rotated
        /// so that the dragged edge aligns anti-parallel with the slot edge.
        /// </summary>
        public static void ComputeCandidatePose(Piece dragged, Edge draggedEdge,
            EdgeSlotRegistry.EdgeSlot slot,
            out Vector2 potentialPos, out Quaternion potentialRot)
        {
            Vector2 dragEdgeDir = (draggedEdge.p2 - draggedEdge.p1).normalized;
            Vector2 slotEdgeDir = (slot.p1 - slot.p2).normalized; // Anti-parallel

            float deltaAngle = Vector2.SignedAngle(dragEdgeDir, slotEdgeDir);
            potentialRot = Quaternion.Euler(0, 0, deltaAngle) * dragged.transform.rotation;

            // Map draggedEdge.p1 → slot.p2 after rotation
            Vector2 rootToP1 = draggedEdge.p1 - (Vector2)dragged.transform.position;
            Vector2 rotatedRootToP1 = Quaternion.Euler(0, 0, deltaAngle) * rootToP1;
            potentialPos = slot.p2 - rotatedRootToP1;
        }

        // ─────────────────────────────────────────────
        //  Edge & Vertex Projection
        // ─────────────────────────────────────────────

        /// <summary>
        /// Projects all edges of the dragged piece to where they WOULD be
        /// at the candidate pose, without actually moving the piece.
        /// </summary>
        private static Edge[] ProjectEdgesAtPose(Piece dragged, Edge[] currentEdges,
            Vector2 candidatePos, Quaternion candidateRot)
        {
            Quaternion currentRot = dragged.transform.rotation;
            Quaternion deltaRot = candidateRot * Quaternion.Inverse(currentRot);

            Edge[] projected = new Edge[currentEdges.Length];
            for (int i = 0; i < currentEdges.Length; i++)
            {
                Vector2 localP1 = currentEdges[i].p1 - (Vector2)dragged.transform.position;
                Vector2 localP2 = currentEdges[i].p2 - (Vector2)dragged.transform.position;

                Vector2 projP1 = candidatePos + (Vector2)(deltaRot * localP1);
                Vector2 projP2 = candidatePos + (Vector2)(deltaRot * localP2);

                projected[i] = new Edge
                {
                    p1 = projP1,
                    p2 = projP2,
                    length = Vector2.Distance(projP1, projP2),
                    index = i
                };
            }
            return projected;
        }

        /// <summary>
        /// Projects all vertices (edge start-points) to their candidate pose positions.
        /// Returns a COMPLETE array — fixing the broken loop bug from Section 2.2.
        /// </summary>
        private static Vector2[] ProjectVerticesAtPose(Piece dragged, Edge[] currentEdges,
            Vector2 candidatePos, Quaternion candidateRot)
        {
            Quaternion currentRot = dragged.transform.rotation;
            Quaternion deltaRot = candidateRot * Quaternion.Inverse(currentRot);

            // BUILD THE ENTIRE ARRAY FIRST (Design Doc Section 2.2 fix)
            Vector2[] verts = new Vector2[currentEdges.Length];
            for (int i = 0; i < currentEdges.Length; i++)
            {
                Vector2 localOffset = currentEdges[i].p1 - (Vector2)dragged.transform.position;
                verts[i] = candidatePos + (Vector2)(deltaRot * localOffset);
            }
            return verts;
        }

        // ─────────────────────────────────────────────
        //  Geometry Confirmation (Section 6.2)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Checks that the incoming piece at its candidate pose does NOT intrude
        /// into any piece that is NOT an expected neighbour.
        /// Shared vertices are excluded from the point-in-polygon test.
        /// </summary>
        private static bool ConfirmNoBodyIntrusion(Vector2[] draggedVertices, Piece dragged,
            EdgeSlotRegistry registry,
            HashSet<Piece> expectedNeighbours, HashSet<Vector2> sharedVertices)
        {
            // Step 1: Compute centroid from the COMPLETE vertex array
            Vector2 draggedCentroid = Vector2.zero;
            for (int i = 0; i < draggedVertices.Length; i++)
                draggedCentroid += draggedVertices[i];
            draggedCentroid /= draggedVertices.Length;

            // Step 2: Build test vertices = all vertices MINUS shared ones
            List<Vector2> testVertices = new List<Vector2>();
            for (int i = 0; i < draggedVertices.Length; i++)
            {
                if (!IsNearAnySharedVertex(draggedVertices[i], sharedVertices))
                    testVertices.Add(draggedVertices[i]);
            }

            // Step 3: Shrink toward centroid for epsilon clearance
            Vector2[] shrunkenDragged = ShrinkVertices(testVertices.ToArray(), draggedCentroid, 0.85f);

            // Step 4: Test against all non-neighbour cluster pieces
            foreach (var kvp in registry.canonicalVertices)
            {
                Piece otherPiece = kvp.Key;
                if (otherPiece == dragged) continue;
                if (expectedNeighbours.Contains(otherPiece)) continue; // KEY CHANGE from Section 5

                Vector2[] otherVerts = kvp.Value;

                // Check A: Do any of our shrunken vertices land inside otherPiece?
                for (int v = 0; v < shrunkenDragged.Length; v++)
                {
                    if (IsPointInPolygon(shrunkenDragged[v], otherVerts))
                    {
                        Debug.Log($"[Snap Rejected] Dragged vertex {v} penetrated {otherPiece.name}");
                        return false;
                    }
                }

                // Check B: Do any of otherPiece's shrunken vertices land inside us?
                Vector2 otherCentroid = Vector2.zero;
                for (int i = 0; i < otherVerts.Length; i++)
                    otherCentroid += otherVerts[i];
                otherCentroid /= otherVerts.Length;

                Vector2[] shrunkenOther = ShrinkVertices(otherVerts, otherCentroid, 0.85f);

                for (int v = 0; v < shrunkenOther.Length; v++)
                {
                    if (IsPointInPolygon(shrunkenOther[v], draggedVertices))
                    {
                        Debug.Log($"[Snap Rejected] {otherPiece.name} vertex {v} penetrated dragged piece");
                        return false;
                    }
                }
            }

            return true;
        }

        // ─────────────────────────────────────────────
        //  Forgiving Mode: Auto-Rotate (Section 7.1)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Finds the nearest valid orientation for the dragged piece by scanning
        /// all OPEN slots in proximity. Rotates the piece to best-align before
        /// the snap search begins.
        /// Only called in Forgiving mode (config.autoRotate == true).
        /// </summary>
        public static void ComputeBestSnapRotation(Piece dragged, EdgeSlotRegistry registry, SnapConfig config)
        {
            Edge[] draggedEdges = GetWorldEdges(dragged);
            if (draggedEdges.Length == 0) return;

            float bestAngleDelta = float.MaxValue;
            float bestDelta = 0f;

            List<EdgeSlotRegistry.EdgeSlot> openSlots = registry.GetOpenSlots();
            float searchRadius = config.snapDistanceThreshold * 2f;

            foreach (var slot in openSlots)
            {
                Vector2 slotMid = (slot.p1 + slot.p2) * 0.5f;

                foreach (var dEdge in draggedEdges)
                {
                    // Length compatibility
                    if (Mathf.Abs(slot.key.length - dEdge.length) > 0.05f) continue;

                    // Rough proximity check
                    Vector2 dragMid = (dEdge.p1 + dEdge.p2) * 0.5f;
                    if (Vector2.Distance(slotMid, dragMid) > searchRadius) continue;

                    // Compute angular delta
                    Vector2 dragDir = (dEdge.p2 - dEdge.p1).normalized;
                    Vector2 slotDirReversed = (slot.p1 - slot.p2).normalized;
                    float deltaAngle = Vector2.SignedAngle(dragDir, slotDirReversed);

                    if (Mathf.Abs(deltaAngle) < config.angularToleranceDegrees * 2f &&
                        Mathf.Abs(deltaAngle) < bestAngleDelta)
                    {
                        bestAngleDelta = Mathf.Abs(deltaAngle);
                        bestDelta = deltaAngle;
                    }
                }
            }

            // Apply the best rotation if we found one
            if (bestAngleDelta < float.MaxValue)
            {
                dragged.transform.rotation = Quaternion.Euler(0, 0, bestDelta) * dragged.transform.rotation;
            }
        }

        // ─────────────────────────────────────────────
        //  Utilities
        // ─────────────────────────────────────────────

        private static Vector2[] ShrinkVertices(Vector2[] vertices, Vector2 centroid, float scaleFactor)
        {
            Vector2[] shrunken = new Vector2[vertices.Length];
            for (int i = 0; i < vertices.Length; i++)
            {
                shrunken[i] = Vector2.Lerp(centroid, vertices[i], scaleFactor);
            }
            return shrunken;
        }

        private static bool IsNearAnySharedVertex(Vector2 point, HashSet<Vector2> sharedVertices)
        {
            foreach (var sv in sharedVertices)
            {
                if (Vector2.Distance(point, sv) < 0.01f)
                    return true;
            }
            return false;
        }

        private static void AddRoundedVertex(HashSet<Vector2> set, Vector2 point)
        {
            Vector2 rounded = new Vector2(
                Mathf.Round(point.x * 1000f) / 1000f,
                Mathf.Round(point.y * 1000f) / 1000f
            );
            set.Add(rounded);
        }

        /// <summary>
        /// Ray-casting point-in-polygon test. Unchanged from v1.
        /// </summary>
        public static bool IsPointInPolygon(Vector2 point, Vector2[] polygon)
        {
            bool isInside = false;
            for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            {
                if (((polygon[i].y > point.y) != (polygon[j].y > point.y)) &&
                    (point.x < (polygon[j].x - polygon[i].x) * (point.y - polygon[i].y) / (polygon[j].y - polygon[i].y) + polygon[i].x))
                {
                    isInside = !isInside;
                }
            }
            return isInside;
        }
    }
}
