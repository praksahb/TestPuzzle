# Tiling Game — Snap System & Gap Detection
## Agent Prompt Document — Version 3 (Final)

---

## HOW TO USE THIS DOCUMENT

This document is a complete specification for rewriting the snap and gap detection systems of a Unity 2D kite tiling game. Read every section before writing any code. All architectural decisions are finalised. Do not deviate from the data structures, algorithms, or file structure described here without flagging it explicitly.

The existing V1 codebase is provided separately. Section 2 explains what is wrong with it. Section 14 contains specific bugs from the V1 code with exact fix instructions — apply all five before proceeding.

**Files you will rewrite:** `CoreCluster.cs`, `GeometrySnapper.cs`, `EdgeSlotRegistry.cs`
**Files you will create:** `SnapConfig.cs`, `SnapResult.cs`, `GapDetector.cs`, `HatPatternMatcher.cs`, `HatMatchResult.cs`
**Files you must not touch:** `Piece.cs`, `InputManager.cs`, `GridManager.cs`, `GameManager.cs`, `PieceSpawner.cs`

---

## 1. Project Overview

A Unity 2D mobile puzzle game where the player snaps kite-shaped polygon tiles onto a growing cluster. The cluster starts with a fixed set of pre-placed kites at the centre. The player drags new kite pieces from floating spawn positions and drops them near the cluster boundary. If the edges align, the piece snaps in.

**Game over condition:** An enclosed gap (empty space fully surrounded by snapped pieces) forms anywhere in the cluster.

**Win condition:** None — the game is infinite. The camera zooms out as the cluster grows.

**Two snap modes exist:**
- **Forgiving** — for the kite game. Auto-rotates pieces, auto-closes all flush edges on snap.
- **Strict** — for the future Hat tile game mode. No auto-rotate, no auto-close, tight tolerances.

A single inspector bool (`enableHatDetection`) on `CoreCluster` controls whether the Hat pattern detection system runs. It defaults to `false`. The kite game always works with it off.

---

## 2. Why the V1 System Must Be Replaced

### 2.1 Architectural Flaw

The V1 `GeometrySnapper` treats snapping as a physics question ("does this polygon overlap that one?"). Snapping is a graph question ("is this edge slot open?"). Polygon overlap tests are ambiguous at shared boundaries, expensive, and accumulate floating point error. An edge slot registry is O(1), deterministic, and drift-free.

### 2.2 Bug: Broken Vertex Array

In V1 `TrySnap()`, the overlap check runs inside the vertex-building loop. The polygon array is partially zero-padded when tests run. This makes every geometry check structurally unreliable.

```csharp
// V1 — BROKEN: overlap check runs while array is still being built
for (int i = 0; i < draggedEdges.Length; i++) {
    draggedVertices[i] = ...;  // only index i is valid
    bool bodyOverlaps = false;
    // ... entire sweep runs here against zero-padded array
}
```

### 2.3 Bug: Transform Drift

V1 reads `transform.position` from snapped pieces every snap. Pieces are parented to `CoreCluster` and positioned via accumulated `Quaternion.Euler` multiplications. Floating point error compounds. By the 8th–10th snap, vertex positions are wrong enough to cause edge-length mismatches.

### 2.4 Bug: Closing Piece Always Rejected

When a piece fills a concave pocket it shares edges with 2–3 existing neighbours. V1 tests ALL neighbours as obstacles. The neighbours' vertices land exactly on the incoming piece's polygon boundary — where `IsPointInPolygon` is undefined — and the snap is rejected. This is the primary cause of "last piece almost always fails."

### 2.5 Bug: Canonical Vertices Captured After Parenting

In V1 `TryPlacePiece()`, canonical vertices are read after `SetParent`. If `CoreCluster` has any transform offset, reparenting shifts the socket world positions. The registry stores wrong midpoints and subsequent snaps find no matching slots (`NoOpenSlotNearby`).

### 2.6 Bug: Anti-Parallel Mapping Assumes Fixed Winding

`ComputeCandidatePose()` always maps `draggedEdge.p1 → slot.p2`. When the player rotates a piece past certain angles, the socket traversal direction effectively reverses and p1/p2 swap roles. The same edge snaps in one rotation but fails in its rotated equivalent.

### 2.7 Bug: EdgeKey Hash Too Fine

`EdgeKey.GetHashCode()` uses a 0.01 grid but equality checks 0.005 proximity. Two keys 0.006 apart can hash to different dictionary buckets and never be compared, even though they would pass equality. This causes `RegisterPieceEdges` to add duplicate OPEN slots instead of closing the shared edge between two pieces.

### 2.8 Bug: Mirroring Is Not Accounted For In Snap Math

Piece mirroring via X/Y axis swipe is an **intentional mechanic** — some tiling configurations require a mirrored piece to fit correctly. The bug is not the mirroring itself but that the snap math ignores mirror state entirely.

When a piece is mirrored on the X axis (eulerAngles.x == 180), a socket that was at world position `(2, 1, 0)` is now at `(2, -1, 0)`. `GetCurrentWorldVertices` reads the flipped position. The edge direction vector is now reversed relative to what the registry expects. `FindBestMatchingOpenSlot` either finds no match or picks the wrong slot. The snap fails or locks the piece at the wrong orientation.

The fix is **not** to strip X/Y rotation — that would break the mirroring mechanic. The fix is to detect mirror state and flip the edge direction vectors before the snap search, so the registry matching operates on consistent directional conventions regardless of mirror state.

```
IsMirrored(piece):
  return piece.transform.eulerAngles.x is near 180
      OR piece.transform.eulerAngles.y is near 180

GetWorldEdges(piece):
  verts = read edgeSockets[i].position as usual
  For each edge i:
    p1 = verts[i], p2 = verts[(i+1) % count]
    If IsMirrored(piece):
      swap p1 and p2   // reverse edge direction to match registry convention
  Return edges with corrected p1/p2 directions
```

The same mirror-aware direction correction must be applied in `ProjectEdgesAtPose` and `ProjectVerticesAtPose` when projecting the dragged piece to its candidate pose. The canonical vertices stored at snap time must also capture the mirrored socket positions correctly — since `GetCurrentWorldVertices` reads live socket world positions (already mirrored), the canonical store naturally captures the correct mirrored geometry.

The registry itself does not need to change — it stores edge midpoints and lengths, both of which are mirror-invariant. Only the direction vectors used for angular matching need the correction.

### 2.9 Bug: Rejection Log Gated Behind Config Flag

The rejection reason log is inside `if (config.emitRejectionEvents)`. In Forgiving mode this is `false`, so all rejections are silent during development.

---

## 3. The Two-Layer Architecture

The new system uses two layers in strict sequence:

**Layer 1 — Edge Slot Registry (Primary)**
Answers: "Is there an open slot here with matching edge length?" This replaces all polygon-based occupancy checks. It is O(1), drift-free, and cannot produce false rejections at shared vertices.

**Layer 2 — Geometry Confirmation (Narrowed Fallback)**
Answers: "Does the body of this piece intrude into a non-neighbouring piece?" Only runs after Layer 1 passes. Excludes expected neighbours and shared vertices entirely — this is what solves the closing piece problem.

---

## 4. Layer 1: Edge Slot Registry

### 4.1 Concept

Every edge in the cluster is registered as either `OPEN` (cluster boundary, free to snap to) or `CLOSED` (shared between two pieces, no further snapping allowed). The registry is the single source of truth for cluster topology.

On a successful snap in Forgiving mode: ALL edges of the incoming piece that align with existing OPEN slots are closed simultaneously. Remaining edges are added as new OPEN slots.

On a successful snap in Strict mode: ONLY the primary matched edge is closed. Other flush edges remain OPEN until the player independently snaps to them.

### 4.2 Data Structures

```
EdgeSlotRegistry                         pure C# class, no MonoBehaviour
└── Dictionary<EdgeKey, EdgeSlot>  slots

EdgeKey                                  dictionary key — identifies an edge uniquely
├── Vector2  midpoint                    rounded to 3 decimal places
└── float    length                      rounded to 2 decimal places

  Constructor: EdgeKey(Vector2 p1, Vector2 p2)
    mid = (p1 + p2) * 0.5
    midpoint = Round(mid, 3 decimal places)
    length = Round(Distance(p1, p2), 2 decimal places)

  Equals(EdgeKey other):
    return Distance(midpoint, other.midpoint) < 0.005f
        && Abs(length - other.length) < 0.05f

  GetHashCode():
    // CRITICAL: hash grid must be COARSER than equality threshold
    // so nearby keys always land in the same bucket
    hx = RoundToInt(midpoint.x * 50f)   // 0.02 grid — coarser than 0.005 threshold
    hy = RoundToInt(midpoint.y * 50f)
    hl = RoundToInt(length * 10f)        // 0.1 grid — coarser than 0.05 threshold
    return (hx * 397) ^ (hy * 17) ^ hl

EdgeSlot                                 one registered edge
├── EdgeKey   key
├── Vector2   p1                         canonical world position, set ONCE, never changed
├── Vector2   p2                         canonical world position, set ONCE, never changed
├── bool      isOpen
├── Piece     ownerPiece
├── int       edgeIndex
└── Piece     occupyingPiece             null if OPEN

CanonicalVertexStore                     lives inside EdgeSlotRegistry
└── Dictionary<Piece, Vector2[]>  canonicalVertices
    Written ONCE at snap time. Never modified.
    ALL geometry reads use this — never piece.transform.
```

### 4.3 Public API

```
void BootstrapFromCluster(List<Piece> initialPieces)
  Called once in CoreCluster.Start() after initial pieces are placed.
  First pass: store canonicalVertices for each piece from current socket positions.
  Second pass: register all edges. If two pieces share the same EdgeKey, mark CLOSED.
  Otherwise mark OPEN. Log OPEN and CLOSED counts on completion.

void RegisterPieceEdges(Piece piece, Vector2[] canonicalVerts)
  Store canonicalVerts for this piece.
  For each edge: if its EdgeKey matches an existing OPEN slot, close that slot.
  If no match, add it as a new OPEN slot.

void CloseSlot(EdgeKey key, Piece occupyingPiece)
  Explicitly close one slot. Used in Strict mode for the primary edge only.

void UnregisterPiece(Piece piece)
  Remove all slots owned by this piece from the dictionary.
  Remove from canonicalVertices.
  Used during Hat phase transition when 8 kites are replaced.

List<EdgeSlot> GetOpenSlots()
  Return all slots where isOpen == true.

EdgeSlot FindBestMatchingOpenSlot(Edge draggedEdge, float threshold)
  For each OPEN slot:
    Skip if Abs(slot.key.length - draggedEdge.length) > 0.05
    Compute midpoint distance between slot and draggedEdge
    Skip if midDist > threshold
    Compute angular penalty: angleDiff between dragDir and reversed slotDir, scaled to [0, 2]
    score = midDist + angularPenalty
  Return slot with lowest score, or null if none within threshold.

List<EdgeSlot> FindAllMatchingOpenSlots(Edge[] projectedEdges, float threshold)
  For each OPEN slot, for each projected edge:
    Skip if length mismatch > 0.05
    Skip if midpoint distance > threshold
    Skip if angleDiff > 30 degrees (generous — this is for neighbour detection, not snap)
    If match: add to results, move to next slot
  Return all matched slots.
  Used to build expectedNeighbours and sharedVertices before geometry sweep.

static Vector2[] GetCurrentWorldVertices(Piece piece)
  Read edgeSockets[i].position for each socket.
  Used ONLY for: (a) bootstrap of initial pieces, (b) the currently-dragged piece.
  Never used for already-snapped pieces — those read from canonicalVertices.
```

---

## 5. The Closing Piece Problem

### 5.1 Root Cause

When piece P fills a pocket bordered by A, B, C:
- Layer 1 matches P's edge to A's OPEN slot — valid
- Candidate pose computed
- At that pose, P's other edges are flush against B and C
- Layer 2 runs against ALL pieces including B and C
- B's vertices land exactly on P's polygon boundary — `IsPointInPolygon` undefined at boundary — false rejection

### 5.2 Solution: Expected Neighbour Detection

Before the geometry sweep, identify every cluster piece that the incoming piece will be flush against at its candidate pose. Exclude all of them from the sweep entirely.

```
ComputeExpectedNeighbours(dragged, candidatePose, registry, threshold):

1. Project ALL edges of dragged to world space at candidatePose
   Use deltaRot + candidatePos, NOT dragged.transform

2. matchedSlots = registry.FindAllMatchingOpenSlots(projectedEdges, threshold)

3. expectedNeighbours = { slot.ownerPiece for slot in matchedSlots }

4. sharedVertices = { slot.p1, slot.p2 for slot in matchedSlots }
                    UNION { projected p1, p2 for each matched dragged edge }
```

### 5.3 Why Skipping Neighbours Is Safe

A piece enters `expectedNeighbours` only if its OPEN slot aligns with the incoming piece's projected edge. Two polygons sharing a flush edge are adjacent, not overlapping. The registry guarantees those slots are OPEN, so no third piece is there. The geometry sweep's only remaining job is catching genuine body intrusion into non-neighbours — which is a real illegal overlap.

---

## 6. Layer 2: Geometry Confirmation

### 6.1 Question

"Does the body of the incoming piece intrude into any non-neighbouring piece?"

### 6.2 Algorithm

```
ConfirmNoBodyIntrusion(draggedVerts, dragged, registry, expectedNeighbours, sharedVertices):

1. Build draggedVertices[] — COMPLETE array, all vertices at candidate pose
   (This array is built ENTIRELY before any test runs — fixes Section 2.2 bug)

2. draggedCentroid = mean of all draggedVertices

3. testVertices = draggedVertices MINUS any vertex within 0.01 of a sharedVertex
   (Shared vertices sit on neighbour boundaries — IsPointInPolygon undefined there)

4. shrunkenTest = Lerp(draggedCentroid, each testVertex, 0.85)
   (15% inward pull gives epsilon clearance from non-neighbour edges)

5. For each piece in registry.canonicalVertices:
     Skip if piece == dragged
     Skip if piece IN expectedNeighbours          ← THE KEY CHANGE

     otherVerts = registry.canonicalVertices[piece]   ← NOT piece.transform

     CHECK A — does dragged intrude into other?
     For each v in shrunkenTest:
       if IsPointInPolygon(v, otherVerts) → return false

     CHECK B — does other intrude into dragged?
     otherCentroid = mean of otherVerts
     shrunkenOther = Lerp(otherCentroid, each otherVert, 0.85)
     For each v in shrunkenOther:
       if IsPointInPolygon(v, draggedVertices) → return false

6. return true
```

### 6.3 IsPointInPolygon

Keep the existing ray-casting implementation unchanged. It is correct for interior points. The changes above ensure it is never called with a point on a polygon boundary.

---

## 7. Snap Modes

### 7.1 SnapConfig ScriptableObject

```csharp
[CreateAssetMenu(fileName = "SnapConfig", menuName = "TilingGame/SnapConfig")]
public class SnapConfig : ScriptableObject
{
    public SnapMode mode;
    public float    snapDistanceThreshold;
    public float    angularToleranceDegrees;
    public bool     autoRotate;
    public bool     autoCloseAdjacentEdges;
    public bool     emitRejectionEvents;
    public int      strictModeThreshold;    // piece count at which to auto-switch to strict
}

public enum SnapMode { Forgiving, Strict }
```

Create two ScriptableObject assets in the project:
- `ForgivingConfig.asset`
- `StrictConfig.asset`

### 7.2 Forgiving Mode Settings

| Parameter | Value |
|---|---|
| snapDistanceThreshold | 0.6 |
| angularToleranceDegrees | 25 |
| autoRotate | true |
| autoCloseAdjacentEdges | true |
| emitRejectionEvents | false |

**Auto-rotate:** Before Layer 1, sanitize the piece's rotation to Z-only (strip X/Y), then find the OPEN slot within `2x snapDistanceThreshold` whose edge requires the smallest Z rotation delta. Apply that rotation to the piece before the snap search begins.

**Auto-close:** On snap confirmation, close ALL matched slots from `FindAllMatchingOpenSlots`, not just the primary.

### 7.3 Strict Mode Settings

| Parameter | Value |
|---|---|
| snapDistanceThreshold | 0.25 |
| angularToleranceDegrees | 8 |
| autoRotate | false |
| autoCloseAdjacentEdges | false |
| emitRejectionEvents | true |

**No auto-rotate:** Snap at whatever rotation the player placed the piece. Reject with `AngularMisalignment` if the angle is wrong.

**No auto-close:** Close only the primary matched edge. Other flush edges stay OPEN.

**Rejection events:** Fire `OnSnapRejected(RejectionReason)` on every rejection so UI can give the player specific feedback.

```csharp
public enum RejectionReason
{
    NoOpenSlotNearby,
    EdgeLengthMismatch,
    AngularMisalignment,
    BodyOverlap,
    SlotOccupied
}
```

---

## 8. Snap Pipeline

### 8.1 Entry Point

`CoreCluster.TryPlacePiece(Piece piece)` is called by `InputManager` when the player releases a piece.

### 8.2 Rotation Sanitization (All Modes)

Before anything else, strip X/Y rotation from the piece:

```csharp
float zAngle = piece.transform.eulerAngles.z;
piece.transform.rotation = Quaternion.Euler(0f, 0f, zAngle);
```

This fixes the X/Y axis rotation bug (Section 2.8). Apply this in `TryPlacePiece` before any other logic.

### 8.3 Forgiving Mode Pipeline

```
1. Sanitize rotation to Z-only (Section 8.2)

2. GeometrySnapper.ComputeBestSnapRotation(piece, registry, config)
   — Sanitizes Z rotation again internally
   — Finds best matching OPEN slot within 2x threshold
   — Applies smallest Z delta to piece.transform.rotation

3. For each edge of dragged piece:
   candidate = registry.FindBestMatchingOpenSlot(edge, threshold)
   Track best score across all edges → primarySlot, bestDraggedEdge

4. If primarySlot == null → reject NoOpenSlotNearby

5. ComputeCandidatePose(dragged, bestDraggedEdge, primarySlot)
   → potentialPos, potentialRot
   Try BOTH p1→slot.p2 and p2→slot.p1 mappings
   Use whichever potentialPos is closer to dragged.transform.position

6. Project ALL dragged edges to candidatePose → projectedEdges[]

7. matchedSlots = registry.FindAllMatchingOpenSlots(projectedEdges, threshold)
   Build expectedNeighbours and sharedVertices from matchedSlots

8. ConfirmNoBodyIntrusion(projectedVerts, dragged, registry, expectedNeighbours, sharedVertices)
   → false: reject BodyOverlap, try next candidate or return fail
   → true: proceed

9. SNAP CONFIRMED:
   piece.transform.position = potentialPos
   piece.transform.rotation = potentialRot

   // Read canonical vertices BEFORE parenting (fixes Section 2.5 bug)
   Vector2[] canonicalVerts = EdgeSlotRegistry.GetCurrentWorldVertices(piece)

   piece.isFloating = false
   piece.transform.SetParent(CoreCluster.transform)

   // Register with already-captured vertices
   registry.RegisterPieceEdges(piece, canonicalVerts)
   // RegisterPieceEdges auto-closes all matched slots (Forgiving)

10. Run GapDetector.Scan(registry)
    → gap found: GameManager.TriggerGameOver()

11. If enableHatDetection:
    HatPatternMatcher.TryFindHat(registry, corePieces, newlySnappedPiece)
    → match found: GameManager.OnHatPatternDetected(result)

12. GameManager.OnPiecePlacedSuccessfully()
    UpdateCameraBounds()
```

### 8.4 Strict Mode Pipeline

```
1. Sanitize rotation to Z-only (Section 8.2)
   (No auto-rotate in Strict mode)

2. For each edge of dragged piece:
   candidate = registry.FindBestMatchingOpenSlot(edge, TIGHT threshold)
   Track best score → primarySlot, bestDraggedEdge

3. If primarySlot == null → fire OnSnapRejected(NoOpenSlotNearby), return false

4. Angular check:
   angleDiff = Abs(SignedAngle(dragEdgeDir, reversedSlotDir))
   If angleDiff > config.angularToleranceDegrees
   → fire OnSnapRejected(AngularMisalignment), return false

5. ComputeCandidatePose → potentialPos, potentialRot (dual mapping as above)

6. Project all edges → projectedEdges[]
   FindAllMatchingOpenSlots → expectedNeighbours, sharedVertices
   (secondary matches identified but NOT auto-closed)

7. ConfirmNoBodyIntrusion → false: fire OnSnapRejected(BodyOverlap), return false

8. SNAP CONFIRMED:
   Move piece, read canonicalVerts BEFORE parenting, parent, register

   // Strict: close primary slot ONLY
   registry.CloseSlot(primarySlot.key, piece)
   // Other matched slots remain OPEN — player must snap to them independently

9. Run GapDetector.Scan(registry)

10. GameManager.OnPiecePlacedSuccessfully()
    UpdateCameraBounds()
```

### 8.5 ComputeCandidatePose — Dual Mapping Fix

```csharp
public static void ComputeCandidatePose(Piece dragged, Edge draggedEdge,
    EdgeSlotRegistry.EdgeSlot slot,
    out Vector2 potentialPos, out Quaternion potentialRot)
{
    Vector2 dragEdgeDir = (draggedEdge.p2 - draggedEdge.p1).normalized;
    Vector2 slotEdgeDir = (slot.p1 - slot.p2).normalized; // anti-parallel

    float deltaAngle = Vector2.SignedAngle(dragEdgeDir, slotEdgeDir);
    potentialRot = Quaternion.Euler(0, 0, deltaAngle) * dragged.transform.rotation;

    Quaternion deltaRot = Quaternion.Euler(0, 0, deltaAngle);
    Vector2 rootToP1 = draggedEdge.p1 - (Vector2)dragged.transform.position;
    Vector2 rootToP2 = draggedEdge.p2 - (Vector2)dragged.transform.position;

    Vector2 rotatedP1 = (Vector2)(deltaRot * rootToP1);
    Vector2 rotatedP2 = (Vector2)(deltaRot * rootToP2);

    // Try both mappings — pick whichever puts piece closer to drop position
    Vector2 candidateA = slot.p2 - rotatedP1;  // p1 maps to slot.p2
    Vector2 candidateB = slot.p1 - rotatedP2;  // p2 maps to slot.p1

    potentialPos = Vector2.Distance(candidateA, (Vector2)dragged.transform.position)
                 < Vector2.Distance(candidateB, (Vector2)dragged.transform.position)
                 ? candidateA : candidateB;
}
```

---

## 9. Gap Detection

### 9.1 Concept

After every snap, OPEN slots form the cluster's boundary graph. A valid cluster has exactly one connected component (single outer boundary loop). An enclosed gap creates two or more components — one outer boundary, one or more inner holes.

### 9.2 GapScanResult

```csharp
public struct GapScanResult
{
    public bool  hasGap;
    public bool  hasNearlyEnclosedGap;
    public float gapArea;
}
```

### 9.3 Algorithm

```
GapDetector.Scan(EdgeSlotRegistry registry, float gapAreaThreshold) → GapScanResult:

1. openSlots = registry.GetOpenSlots()
   If openSlots.Count == 0 → return no gap

2. Build adjacency graph:
   nodes = Dictionary<Vector2, List<EdgeSlot>>
     key = endpoint rounded to 3 decimal places
   For each slot: add slot to adjacency lists of both endpoints
   Deduplicate nodes within 0.005 of each other (same threshold as EdgeKey)

3. BFS/DFS to find connected components:
   visited = HashSet of visited node keys
   components = List<List<EdgeSlot>>
   For each unvisited node:
     Walk all reachable edges → collect into one component
   If components.Count == 1 → return { hasGap = false }

4. outerComponent = component with largest Shoelace area
   For each remaining (inner) component:
     area = ComputeLoopArea(component via Shoelace)
     isClosed = no node in this component has degree 1
     If area > gapAreaThreshold AND isClosed:
       return { hasGap = true, gapArea = area }
     Else:
       return { hasNearlyEnclosedGap = true }

5. return { hasGap = false }
```

### 9.4 Shoelace Area

```
ComputeLoopArea(List<EdgeSlot> loop):
1. Walk edge adjacency to reconstruct ordered vertex list
2. area = 0.5 * Abs( Sum_i( x_i * y_{i+1} - x_{i+1} * y_i ) )
```

### 9.5 Edge Cases

**Pinch points:** A degree-4 node (four OPEN edges meeting at one vertex) correctly produces two separate components. No special handling needed.

**Nearly-enclosed gaps:** A component with one degree-1 node has one open edge remaining. This is NOT game over — warn the player.

**Endpoint deduplication:** Must use the same 0.005 threshold as `EdgeKey` equality. If two nodes within 0.005 are not merged, they create false disconnections and phantom gap detections.

---

## 10. Hat Pattern Detection

### 10.1 Inspector Flag

```csharp
// On CoreCluster.cs
[Header("Hat Pattern Detection")]
[SerializeField] private bool enableHatDetection = false;
```

Default is `false`. The kite game runs as pure kite tiling forever with this off. Zero overhead. Flip to `true` only in the `HatTransitionTest` scene.

### 10.2 Overview

The Hat tile is composed of exactly 8 kite pieces in a specific shared-edge topology. When the 8th kite completes the pattern, the system:
1. Destroys the 8 kite GameObjects
2. Instantiates a Hat prefab at their centroid
3. Registers the Hat into the registry in place of the 8 kites
4. Switches `PieceSpawner` to spawn Hat prefabs
5. Switches `CoreCluster.activeConfig` to `StrictConfig`

### 10.3 HatPatternMatcher

Pure C# class. No MonoBehaviour. Called after every successful kite snap, only when `enableHatDetection == true`.

```
HatPatternMatcher

  static readonly (int,int)[] HatAdjacency
    — Derived by the agent from the Hat reference image
    — Encodes which of the 8 kites (K0–K7) share a CLOSED edge
    — Topology only, not positions or rotations

  HatMatchResult TryFindHat(EdgeSlotRegistry registry, List<Piece> corePieces, Piece newPiece)
    — Only the newly snapped piece is used as seed
      (Hat can only complete when the 8th piece snaps)
    — BFS over CLOSED edges from newPiece, depth limit 7
    — If connected component < 8 → return not found
    — For each 8-piece subset containing newPiece:
        Build adjacency from CLOSED edges
        Run IsIsomorphic(candidate, HatAdjacency)
        If match → return HatMatchResult with matched pieces, centroid, rotation
    — Return not found
```

### 10.4 Graph Isomorphism

8 nodes, fixed reference graph. 8! = 40,320 permutations, each O(edges). Runs in microseconds.

```
IsIsomorphic(candidateAdj, HatAdjacency):
  degree-sequence pre-filter: each candidate node must match reference node degree
  For each permutation P of [0..7]:
    For each (Ki, Kj) in HatAdjacency:
      If (P[Ki], P[Kj]) not in candidateAdj → fail this permutation
    If all edges pass → return true, store permutation as isomorphismMapping
  Return false
```

### 10.5 Phase Transition

Fires immediately when `TryFindHat` returns a match.

```
GameManager.OnHatPatternDetected(HatMatchResult result):

1. InputManager.SetEnabled(false)

2. For each piece in result.matchedKites:
   registry.UnregisterPiece(piece)
   corePieces.Remove(piece)
   Destroy(piece.gameObject)

3. hatGO = Instantiate(hatPrefab, result.centroid, Quaternion.Euler(0,0,result.rotation))
   hatGO.transform.SetParent(CoreCluster.transform)
   hatPiece = hatGO.GetComponent<Piece>()

4. hatCanonicalVerts = ComputeHatCanonicalVerts(result)
   // Collect OPEN edge endpoints of the 8 kites, order clockwise around centroid
   registry.RegisterPieceEdges(hatPiece, hatCanonicalVerts)
   corePieces.Add(hatPiece)

5. CoreCluster.activeConfig = strictSnapConfig
   PieceSpawner.SetPiecePrefab(hatPrefab)

6. InputManager.SetEnabled(true)
   UIManager.PlayHatTransitionEffect()   // optional, non-blocking
```

### 10.6 New Files for Hat System

```
HatPatternMatcher.cs   Pure C#. No MonoBehaviour.
HatMatchResult.cs      Struct: bool found, List<Piece> matchedKites,
                               Vector2 centroid, float rotation, int[] isomorphismMapping
```

### 10.7 Additions to Existing Files for Hat System

```
EdgeSlotRegistry.cs    ADD: UnregisterPiece(Piece piece)
GameManager.cs         ADD: OnHatPatternDetected(HatMatchResult result)
                       ADD: [SerializeField] GameObject hatPrefab
                       ADD: [SerializeField] SnapConfig strictSnapConfig
CoreCluster.cs         ADD: Call TryFindHat() inside snap success path when enableHatDetection
```

---

## 11. Integration Map

### 11.1 Files to Rewrite Completely

```
EdgeSlotRegistry.cs
  All logic from Section 4.
  Include CanonicalVertexStore (Dictionary<Piece, Vector2[]> canonicalVertices) internally.
  Include UnregisterPiece() for Hat system.

GeometrySnapper.cs
  TrySnap() → full pipeline per Section 8
  ComputeCandidatePose() → dual mapping per Section 8.5
  ProjectEdgesAtPose() — project all edges to candidate pose
  ProjectVerticesAtPose() — build COMPLETE vertex array before any testing
  ComputeExpectedNeighbours() — Section 5.2
  ConfirmNoBodyIntrusion() — Section 6.2
  ComputeBestSnapRotation() — Forgiving auto-rotate with Z sanitization
  IsPointInPolygon() — keep unchanged
  GetWorldEdges() — for dragged piece only (live transform)
  GetCanonicalEdges() — for snapped pieces (from canonicalVertices)

CoreCluster.cs
  Registry field, BootstrapRegistry(), TryPlacePiece() with all fixes,
  Hat detection call, gap detection call, camera zoom (unchanged)
```

### 11.2 Files to Create

```
SnapConfig.cs          ScriptableObject — Section 7.1
SnapResult.cs          Struct: bool success, Vector2 targetPos, Quaternion targetRot,
                               List<EdgeSlot> closedSlots, RejectionReason reason
GapDetector.cs         Static class — Section 9
HatPatternMatcher.cs   Section 10.3
HatMatchResult.cs      Section 10.6
```

### 11.3 Files That Must Not Change

```
Piece.cs, InputManager.cs, GridManager.cs, GameManager.cs, PieceSpawner.cs
```

---

## 12. V1 Bug Fix Checklist

The agent must verify all five fixes are applied before marking any file complete.

| # | Bug | Location | Fix |
|---|---|---|---|
| 1 | Canonical verts read after parenting | `CoreCluster.TryPlacePiece()` | Read verts before `SetParent`, then register |
| 2 | Anti-parallel mapping assumes fixed winding | `GeometrySnapper.ComputeCandidatePose()` | Try both p1→slot.p2 and p2→slot.p1, pick closer |
| 3 | EdgeKey hash grid too fine | `EdgeSlotRegistry.EdgeKey.GetHashCode()` | Use 50f (0.02 grid) for midpoint, 10f (0.1 grid) for length |
| 4 | Rejection log gated by emitRejectionEvents | `CoreCluster.TryPlacePiece()` | Always log; only fire event when flag is true |
| 5 | Mirroring not accounted for in snap math | `GeometrySnapper.GetWorldEdges()`, `ProjectEdgesAtPose()`, `ProjectVerticesAtPose()` | Detect mirror state (X or Y eulerAngle ≈ 180) and swap p1/p2 direction on affected edges before matching |

---

## 13. Open Questions

Resolve these by inspecting existing scripts before writing code.

1. **Edge snappability:** Are all 4 kite edges snappable, or are some restricted (e.g. interior spine)? Determines which edges `RegisterPieceEdges` adds as OPEN.

2. **Edge length variety:** Do kites have two distinct edge lengths (short/long) or are all four the same? Affects how much weight `EdgeKey` gives to length matching.

3. **Bootstrap timing:** Confirm `CoreCluster.Start()` is the right place for `BootstrapRegistry()`. The bootstrap must identify outer edges as OPEN and shared edges as CLOSED correctly.

4. **SnapConfig assets:** Create `ForgivingConfig.asset` and `StrictConfig.asset` as ScriptableObject instances with the values from Section 7.2 and 7.3.

5. **Pinch point policy:** If the cluster forms a figure-8 (boundary touching itself at one vertex), is this game over or allowed? This determines whether `GapDetector` flags degree-4 nodes.

6. **Gap area threshold:** Default recommendation: 50% of one kite's area, computed from the first entry in `canonicalVertices` at bootstrap time.

---

## 14. Implementation Order

Build in this order. Each step is independently testable before the next begins.

1. `SnapConfig.cs` + both preset assets — no dependencies
2. `SnapResult.cs` + `RejectionReason` enum — no dependencies
3. `EdgeSlotRegistry.cs` — pure C#, test with unit tests before Unity wiring
4. Fix #3 (hash) and confirm `BootstrapFromCluster` works with a 2-piece test case
5. `CoreCluster.cs` — `BootstrapRegistry()`, minimal `TryPlacePiece()` with fixes #1, #4, #5
6. `GeometrySnapper.cs` — `TrySnap()` with registry as primary, fix #2, fix #5 in auto-rotate
7. Wire and test: snap 3–4 pieces, confirm no `NoOpenSlotNearby` after piece 2
8. Add `ComputeExpectedNeighbours()` + updated `ConfirmNoBodyIntrusion()` — test closing piece
9. `GapDetector.cs` — test with hand-constructed slot lists before wiring
10. Wire `GapDetector.Scan()` into snap success path
11. `HatPatternMatcher.cs` + `HatMatchResult.cs` — test with mock registry data
12. Wire Hat detection behind `enableHatDetection` flag
13. Playtesting pass — tune `snapDistanceThreshold`, `angularToleranceDegrees`, gap area threshold

---

*Document Version 3 — Final. All decisions confirmed by project owner. No further design changes expected before implementation.*
