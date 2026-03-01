using UnityEngine;
using System.Collections.Generic;

namespace TMKOC.Games.TilingGame
{
    public static class GeometrySnapper
    {
        public struct Edge
        {
            public Vector2 p1;
            public Vector2 p2;
            public float length;
            public int index;
        }

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
                int idx1 = i;
                int idx2 = (i + 1) % count;

                Transform t1 = piece.edgeSockets[idx1];
                Transform t2 = piece.edgeSockets[idx2];

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

            EdgeSlotRegistry.EdgeSlot primarySlot = null;
            Edge bestDraggedEdge = default;
            float bestScore = config.snapDistanceThreshold;

            foreach (var dEdge in draggedEdges)
            {
                var candidate = registry.FindBestMatchingOpenSlot(dEdge, config.snapDistanceThreshold);
                if (candidate != null)
                {
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

            Vector2 potentialPos;
            Quaternion potentialRot;
            ComputeCandidatePose(dragged, bestDraggedEdge, primarySlot, out potentialPos, out potentialRot);

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

            Edge[] projectedEdges = ProjectEdgesAtPose(dragged, draggedEdges, potentialPos, potentialRot);
            List<EdgeSlotRegistry.EdgeSlot> matchedSlots = registry.FindAllMatchingOpenSlots(projectedEdges, config.snapDistanceThreshold);

            HashSet<Piece> expectedNeighbours = new HashSet<Piece>();
            HashSet<Vector2> sharedVertices = new HashSet<Vector2>();

            foreach (var slot in matchedSlots)
            {
                expectedNeighbours.Add(slot.ownerPiece);
                AddRoundedVertex(sharedVertices, slot.p1);
                AddRoundedVertex(sharedVertices, slot.p2);
            }

            foreach (var pEdge in projectedEdges)
            {
                AddRoundedVertex(sharedVertices, pEdge.p1);
                AddRoundedVertex(sharedVertices, pEdge.p2);
            }

            Vector2[] projectedVerts = ProjectVerticesAtPose(dragged, draggedEdges, potentialPos, potentialRot);

            if (!ConfirmNoBodyIntrusion(projectedVerts, dragged, registry, expectedNeighbours, sharedVertices))
            {
                result.reason = RejectionReason.BodyOverlap;
                return result;
            }

            result.success = true;
            result.targetPos = potentialPos;
            result.targetRot = potentialRot;
            result.closedSlots = matchedSlots;
            result.projectedVertices = projectedVerts; // Already exact, avoids transform drift issues

            return result;
        }

        public static void ComputeCandidatePose(Piece dragged, Edge draggedEdge, EdgeSlotRegistry.EdgeSlot slot, out Vector2 potentialPos, out Quaternion potentialRot)
        {
            Vector2 dragEdgeDir = (draggedEdge.p2 - draggedEdge.p1).normalized;
            Vector2 slotEdgeDir = (slot.p1 - slot.p2).normalized; // Anti-parallel

            float deltaAngle = Vector2.SignedAngle(dragEdgeDir, slotEdgeDir);
            potentialRot = Quaternion.Euler(0, 0, deltaAngle) * dragged.transform.rotation;

            // --- BUG FIX: Floating Point Rotation Drift ---
            float rawZ = potentialRot.eulerAngles.z;
            float snappedZ = Mathf.Round(rawZ / 30f) * 30f;
            
            potentialRot = Quaternion.Euler(0f, 0f, snappedZ);

            // Use the actual clamped rotation to calculate translation
            Quaternion actualDeltaRot = potentialRot * Quaternion.Inverse(dragged.transform.rotation);
            
            Vector2 rootToP1 = draggedEdge.p1 - (Vector2)dragged.transform.position;
            Vector2 rootToP2 = draggedEdge.p2 - (Vector2)dragged.transform.position;

            Vector2 rotatedP1 = (Vector2)(actualDeltaRot * rootToP1);
            Vector2 rotatedP2 = (Vector2)(actualDeltaRot * rootToP2);

            Vector2 candidateA = slot.p2 - rotatedP1; // p1 maps to slot.p2
            Vector2 candidateB = slot.p1 - rotatedP2; // p2 maps to slot.p1

            float distA = Vector2.Distance(candidateA, (Vector2)dragged.transform.position);
            float distB = Vector2.Distance(candidateB, (Vector2)dragged.transform.position);

            potentialPos = distA < distB ? candidateA : candidateB;
        }

        private static Edge[] ProjectEdgesAtPose(Piece dragged, Edge[] currentEdges, Vector2 candidatePos, Quaternion candidateRot)
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

        private static Vector2[] ProjectVerticesAtPose(Piece dragged, Edge[] currentEdges, Vector2 candidatePos, Quaternion candidateRot)
        {
            Quaternion currentRot = dragged.transform.rotation;
            Quaternion deltaRot = candidateRot * Quaternion.Inverse(currentRot);

            // Constraint 1 Fix #2.2: Build the entire complete array before any testing occurs.
            // The method signature returns Vector2[] with no side effects, making it structurally robust.
            Vector2[] verts = new Vector2[currentEdges.Length];
            for (int i = 0; i < currentEdges.Length; i++)
            {
                Vector2 localOffset = currentEdges[i].p1 - (Vector2)dragged.transform.position;
                verts[i] = candidatePos + (Vector2)(deltaRot * localOffset);
            }
            
            for (int i = 0; i < verts.Length; i++)
            {
                verts[i] = new Vector2(
                    Mathf.Round(verts[i].x * 1000f) / 1000f,
                    Mathf.Round(verts[i].y * 1000f) / 1000f
                );
            }
            return verts;
        }

        private static bool ConfirmNoBodyIntrusion(Vector2[] draggedVertices, Piece dragged, EdgeSlotRegistry registry, HashSet<Piece> expectedNeighbours, HashSet<Vector2> sharedVertices)
        {
            Vector2 draggedCentroid = Vector2.zero;
            for (int i = 0; i < draggedVertices.Length; i++)
                draggedCentroid += draggedVertices[i];
            draggedCentroid /= draggedVertices.Length;

            List<Vector2> testVertices = new List<Vector2>();
            for (int i = 0; i < draggedVertices.Length; i++)
            {
                if (!IsNearAnySharedVertex(draggedVertices[i], sharedVertices))
                    testVertices.Add(draggedVertices[i]);
            }

            Vector2[] shrunkenDragged = ShrinkVertices(testVertices.ToArray(), draggedCentroid, 0.85f);

            foreach (var kvp in registry.canonicalVertices)
            {
                Piece otherPiece = kvp.Key;
                if (otherPiece == dragged) continue;
                if (expectedNeighbours.Contains(otherPiece)) continue;

                // Constraint 2: Read canonical vertices ONLY from registry.canonicalVertices, NEVER piece.transform.
                // Do not modify this lookup logic.
                Vector2[] otherVerts = kvp.Value; 

                for (int v = 0; v < shrunkenDragged.Length; v++)
                {
                    if (IsPointInPolygon(shrunkenDragged[v], otherVerts))
                    {
                        Debug.Log($"[Snap Rejected] Dragged vertex {v} penetrated {otherPiece.name}");
                        return false;
                    }
                }

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

        public static void ComputeBestSnapRotation(Piece dragged, EdgeSlotRegistry registry, SnapConfig config)
        {
            float zAngle = dragged.transform.eulerAngles.z;
            float snappedZ = Mathf.Round(zAngle / 30f) * 30f;
            dragged.transform.rotation = Quaternion.Euler(0f, 0f, snappedZ);
            Physics2D.SyncTransforms(); // force child sockets to update before edge scanning

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
                    if (Mathf.Abs(slot.key.length - dEdge.length) > 0.05f) continue;

                    Vector2 dragMid = (dEdge.p1 + dEdge.p2) * 0.5f;
                    if (Vector2.Distance(slotMid, dragMid) > searchRadius) continue;

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

            if (bestAngleDelta < float.MaxValue)
            {
                dragged.transform.rotation = Quaternion.Euler(0, 0, bestDelta) * dragged.transform.rotation;
                
                float rawZBest = dragged.transform.eulerAngles.z;
                float snappedZBest = Mathf.Round(rawZBest / 30f) * 30f;
                
                dragged.transform.rotation = Quaternion.Euler(0f, 0f, snappedZBest);
            }
        }

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
