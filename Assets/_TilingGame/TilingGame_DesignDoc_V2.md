# Tiling Game — Snap System Redesign & Gap Detection
## Design Document v2

---

## 1. Context & Scope

This document is the authoritative specification for rewriting the snapping and gap detection systems. It is intended to be passed directly to a code agent as a prompt. All architectural decisions have been finalised by the project owner prior to this version.

### 1.1 What This Document Covers

- Why the current `GeometrySnapper` degrades over time
- The Edge Slot Registry — the new primary snap validator
- The Closing Piece Problem and its fix
- The Geometry Confirmation layer — narrowed and corrected
- Two snap modes: **Forgiving** and **Strict**
- Gap detection via open edge graph traversal
- Integration map: what changes in which file
- Data flow diagrams for both snap modes
- Open questions the agent must resolve before writing code

### 1.2 What This Document Does Not Cover

- Visual tweens, floating behaviour, or input handling (unchanged)
- `GridManager` hex coordinate logic (unchanged)
- Camera zoom logic (unchanged)
- The Hat tile game mode (future scope — Strict mode is designed to support it)

---

## 2. Why the Current System Degrades

The current `GeometrySnapper` has two compounding bugs and one architectural flaw.

### 2.1 Architectural Flaw

It treats every snap as a **physics question** ("does this polygon overlap that one?") when snapping is fundamentally a **graph question** ("is this edge slot open and unoccupied?"). Polygon overlap tests are expensive, ambiguous at shared boundaries, and accumulate error. A slot registry is O(1) lookup, deterministic, and drift-free.

### 2.2 Bug: Broken Vertex Loop

In `TrySnap()`, the `draggedVertices` array is only **partially filled** when the overlap check begins. The vertex-building `for` loop contains the entire overlap sweep inside it, so the polygon being tested has `Vector2.zero` entries for all vertices not yet processed. This makes the geometry test structurally unreliable regardless of tolerances.

```csharp
// CURRENT (broken) — overlap check runs per-vertex before array is complete
for (int i = 0; i < draggedEdges.Length; i++) {
    draggedVertices[i] = ...; // only vertex i is filled
    bool bodyOverlaps = false;
    // ... entire sweep runs here against a zero-padded array
}

// REQUIRED — build entire array first, then sweep
for (int i = 0; i < draggedEdges.Length; i++) {
    draggedVertices[i] = ...;
}
// sweep runs here against a complete array
```

### 2.3 Bug: Transform Drift

`GetWorldEdges()` reads `transform.position` and `transform.rotation` from snapped pieces every time. Since snapped pieces are parented to `CoreCluster` and their transforms are set via accumulated `Quaternion.Euler` multiplications, floating point error compounds with each subsequent snap. By the 8th–10th piece, vertex positions derived from transforms have drifted enough to cause edge-length mismatches and midpoint misalignment.

### 2.4 The Closing Piece Problem

When a piece fills a concave pocket, it shares edges with two or more existing neighbours simultaneously. The current sweep tests ALL neighbours as obstacles — including the ones the incoming piece is intentionally flush against. Those neighbours' vertices land on or inside the incoming piece's polygon boundary exactly where `IsPointInPolygon` is undefined, triggering a false rejection. This is the primary cause of the "last piece almost always rejected" behaviour seen in testing.

---

## 3. The Two-Layer Architecture

The replacement system uses two layers in sequence:

**Layer 1 — Edge Slot Registry (Primary)**
Answers: "Is there an open slot here, and is its length compatible?" This replaces all occupancy checking. It is O(1) per query, drift-free, and cannot produce false rejections at shared vertices.

**Layer 2 — Geometry Confirmation (Fallback)**
Answers: "Does the body of this piece intrude into a non-neighbouring piece?" This is only reached after Layer 1 passes. It is narrowed to exclude expected neighbours and shared vertices, eliminating the corner-vertex ambiguity.

---

## 4. Layer 1: Edge Slot Registry

### 4.1 Core Concept

When a piece snaps into the cluster, all of its edges are registered into a central dictionary on `CoreCluster`. Each edge is in one of two states:

```
OPEN   — part of the cluster boundary, available for a new piece to snap to
CLOSED — shared between two pieces, no further snapping permitted
```

On a successful snap: the primary matched slot transitions OPEN to CLOSED. All other edges of the incoming piece that align with existing OPEN slots also transition CLOSED (auto-close in Forgiving mode; primary edge only in Strict mode — see Section 7). Remaining unmatched edges of the incoming piece are registered as new OPEN slots.

### 4.2 Data Structures

```
EdgeSlotRegistry                       (component on CoreCluster)
└── Dictionary<EdgeKey, EdgeSlot>  slots

EdgeKey
├── Vector2  midpoint                  rounded to 3 decimal places
└── float    length                    rounded to 2 decimal places

EdgeSlot
├── EdgeKey   key
├── Vector2   p1                       canonical world position, set once at registration
├── Vector2   p2                       canonical world position, set once at registration
├── bool      isOpen
├── Piece     ownerPiece
├── int       edgeIndex                which edge index on ownerPiece
└── Piece     occupyingPiece           null if OPEN; set to incoming piece when CLOSED

CanonicalVertexStore                   (component on CoreCluster)
└── Dictionary<Piece, Vector2[]>  pieceVertices
    └── Written ONCE at snap time, never modified thereafter
```

**Why midpoint as key:** The midpoint of a correctly matched edge pair is the same world position regardless of which piece it belongs to or which direction it was approached from. This makes it winding-order agnostic and eliminates the vertex-pair matching ambiguity in the current system.

**Why round floats:** Prevents `(1.00001, 2.0)` and `(0.99999, 2.0)` from being treated as different slots due to floating point drift across frames.

**Why CanonicalVertexStore:** All geometry queries — Layer 2 sweep, gap detection, neighbour identification — read from this store rather than from `piece.transform`. Transform drift is completely isolated to the visual layer. The math never drifts.

### 4.3 EdgeKey Equality Rules

```
Two EdgeKeys are EQUAL if:
  distance(midpoint_A, midpoint_B) < 0.005 world units
  AND
  abs(length_A - length_B) < 0.05 world units
```

This replaces the current `0.1f` length tolerance in `GetWorldEdges`.

### 4.4 Public API

```
EdgeSlotRegistry
  void  BootstrapFromCluster(List<Piece> initialPieces)
        Called once at startup to register the starting cluster's outer edges as OPEN
        and inner shared edges as CLOSED.

  void  RegisterPieceEdges(Piece piece, Vector2[] canonicalVertices)
        Registers all edges of a newly snapped piece. Edges that match existing OPEN
        slots are closed immediately. All others are added as OPEN.

  void  CloseSlot(EdgeKey key, Piece occupyingPiece)
        Marks a slot CLOSED explicitly (used in Strict mode for primary edge only).

  List<EdgeSlot>  GetOpenSlots()
        Returns all currently OPEN slots.

  EdgeSlot?  FindBestMatchingOpenSlot(Edge draggedEdge, float threshold)
        Scores all OPEN slots by (midpoint distance + angular penalty).
        Returns the best candidate within threshold, or null if none found.

  List<EdgeSlot>  FindAllMatchingOpenSlots(Edge[] projectedEdges, float threshold)
        Given all projected edges of the incoming piece at its candidate pose,
        returns every OPEN slot that aligns with any of them.
        Used to build expectedNeighbours and sharedVertices before the geometry sweep.
```

---

## 5. The Closing Piece Problem — Full Solution

### 5.1 Root Cause

When piece P fills a pocket bordered by pieces A, B, and C:
- Layer 1 matches P's edge 0 to A's open slot — valid
- Candidate pose is computed
- At that pose, P's edge 1 is flush against B and P's edge 2 is flush against C
- The geometry sweep runs against ALL corePieces including B and C
- B's vertices land on P's polygon boundary — `IsPointInPolygon` returns undefined at boundary — sweep incorrectly flags overlap — snap rejected

### 5.2 Expected Neighbour Detection

Before running the geometry sweep, identify all pieces the incoming piece will be flush against at its candidate pose. These are **expected neighbours** and must be excluded from the sweep.

```
ComputeExpectedNeighbours(draggedPiece, candidatePose):

1. Project ALL edges of draggedPiece to world space at candidatePose
   using CanonicalVertexStore offsets + deltaRot (not transform)

2. Call registry.FindAllMatchingOpenSlots(projectedEdges, threshold)
   Returns: List<EdgeSlot> matchedSlots

3. expectedNeighbours = { slot.ownerPiece for each slot in matchedSlots }

4. sharedVertices = all p1/p2 from matchedSlots
                    UNION all projected endpoints of matched dragged edges
```

### 5.3 Why This Is Safe

A piece only enters `expectedNeighbours` if one of its registered OPEN slots aligns with the incoming piece's projected edge. Two polygons sharing a flush edge are adjacent, not overlapping. The registry guarantees those slots are OPEN, meaning no other piece is already occupying that position. Therefore skipping expected neighbours in the geometry sweep cannot permit any illegal configuration. The sweep's only remaining job is catching genuine body intrusion into non-neighbouring pieces.

---

## 6. Layer 2: Geometry Confirmation

### 6.1 The Narrowed Question

> "Does the body of the incoming piece intrude into any piece that is NOT an expected neighbour?"

### 6.2 Algorithm

```
ConfirmNoBodyIntrusion(draggedPiece, candidatePose, expectedNeighbours, sharedVertices):

1. Build draggedVertices[] — ALL projected vertices at candidatePose
   This must be a complete array before any testing begins (fixes the broken loop bug)

2. Compute draggedCentroid from complete draggedVertices[]

3. Build testVertices = draggedVertices[] MINUS any vertex in sharedVertices
   Shared vertices lie exactly on neighbour boundaries and are undefined for ray-cast

4. Shrink testVertices toward draggedCentroid by factor 0.85
   Provides epsilon clearance from edges of non-neighbours

5. For each otherPiece in corePieces:
     IF otherPiece == draggedPiece        → skip
     IF otherPiece IN expectedNeighbours  → skip  (KEY CHANGE)

     otherVertices[] = read from CanonicalVertexStore, NOT from transform

     Check A — incoming piece penetrates otherPiece:
     For each v in shrunken testVertices:
       if IsPointInPolygon(v, otherVertices) → return FAIL

     Check B — otherPiece penetrates incoming piece:
     otherCentroid = mean of otherVertices
     shrunkenOther = shrink otherVertices toward otherCentroid by 0.85
     For each v in shrunkenOther:
       if IsPointInPolygon(v, draggedVertices) → return FAIL

6. Return PASS
```

### 6.3 Comparison with Current Implementation

| Aspect | Current | New |
|---|---|---|
| Tested neighbours | ALL corePieces | Non-neighbours only |
| Tested vertices | All dragged vertices (partially-built array) | Complete array minus sharedVertices |
| Vertex source for others | `piece.transform` (drifts) | `CanonicalVertexStore` (fixed) |
| Closing piece scenario | Always rejects | Correctly passes |
| Corner vertex ambiguity | Undefined, random result | Shared vertices excluded |
| Fallback on fail | None — single pass | Tries next-best registry candidate |

---

## 7. Snap Modes

Both modes share the same Edge Slot Registry and Geometry Confirmation architecture. They differ in thresholds, auto-rotate behaviour, and auto-close behaviour. All parameters are exposed via a `SnapConfig` ScriptableObject so swapping modes requires no code changes.

```csharp
public enum SnapMode { Forgiving, Strict }

// ScriptableObject asset — create two preset assets in the project
[CreateAssetMenu]
public class SnapConfig : ScriptableObject {
    public SnapMode  mode;
    public float     snapDistanceThreshold;
    public float     angularToleranceDegrees;
    public bool      autoRotate;
    public bool      autoCloseAdjacentEdges;
    public bool      emitRejectionEvents;
}
```

`CoreCluster` holds a reference to the active `SnapConfig`. Swapping game modes means swapping the ScriptableObject reference — no code changes required.

### 7.1 Forgiving Mode

**Intended for:** Kite tiling game. Default mode. Casual / freeform play.

| Parameter | Recommended Starting Value |
|---|---|
| `snapDistanceThreshold` | 0.6 world units |
| `angularToleranceDegrees` | 25 degrees |
| `autoRotate` | true |
| `autoCloseAdjacentEdges` | true |
| `emitRejectionEvents` | false |

**Auto-rotate behaviour:**
Before Layer 1 scoring, the system computes the nearest valid orientation for the dragged piece by finding the OPEN slot in proximity that produces the smallest angular delta from the current rotation. The piece is rotated to that orientation before the snap candidate search begins. The player does not need to manually orient.

```
ComputeBestSnapRotation(dragged, registry, config):
  For each OPEN slot within rough proximity (2x snapDistanceThreshold):
    For each edge of dragged:
      deltaAngle = SignedAngle(dragEdgeDir, reversed slotEdgeDir)
      If abs(deltaAngle) < config.angularToleranceDegrees * 2:
        Add (deltaAngle, slot) to candidates
  Sort candidates by abs(deltaAngle)
  Apply rotation of best candidate to dragged before Layer 1 runs
```

**Auto-close behaviour:**
On snap confirmation, `FindAllMatchingOpenSlots` is called against ALL projected edges of the incoming piece. Every matching OPEN slot is closed, not just the primary. The piece settles flush against all neighbours simultaneously with no further player action required.

### 7.2 Strict Mode

**Intended for:** Hat tile game mode. Challenge / puzzle play. Accurate player feedback required.

| Parameter | Recommended Starting Value |
|---|---|
| `snapDistanceThreshold` | 0.25 world units |
| `angularToleranceDegrees` | 8 degrees |
| `autoRotate` | false |
| `autoCloseAdjacentEdges` | false |
| `emitRejectionEvents` | true |

**No auto-rotate:** The piece snaps at the rotation the player has placed it. If orientation is wrong, the snap is rejected and `OnSnapRejected` is fired with `RejectionReason.AngularMisalignment`. The player must manually orient using the circular drag rotation gesture.

**No auto-close:** Only the primary matched edge (the one closest to the player's drop position) is closed on snap. Other flush edges visible at that pose are NOT automatically closed. Each shared edge must be independently snapped by the player aligning the piece to that specific neighbour and re-releasing.

**Rejection feedback:** Every rejection fires `OnSnapRejected(RejectionReason reason)` so the UI layer can give the player specific, actionable feedback.

```csharp
public enum RejectionReason {
    NoOpenSlotNearby,
    EdgeLengthMismatch,
    AngularMisalignment,
    BodyOverlap,
    SlotOccupied
}
```

---

## 8. Gap Detection

### 8.1 Concept

After every successful snap, the OPEN slots in the registry form a graph of the cluster's boundary. In a valid cluster with no enclosed gaps, this graph has exactly **one connected component** — a single outer boundary loop. If the cluster encloses a gap, the graph splits into two or more components: one outer boundary loop and one or more inner hole loops.

### 8.2 Data Structure

```
OpenEdgeGraph
├── nodes: Dictionary<Vector2, List<EdgeSlot>>
│           key = endpoint position rounded to 3 decimal places
│           value = all OPEN edges touching this endpoint
└── edges: List<EdgeSlot>  (all OPEN slots from registry)
```

### 8.3 Algorithm

```
GapDetector.Scan(registry) → GapScanResult:

1. openSlots = registry.GetOpenSlots()

2. Build OpenEdgeGraph:
   For each slot in openSlots:
     Add slot.p1 and slot.p2 as nodes (deduplicate by proximity < 0.005)
     Add slot to adjacency list of both nodes

3. Find connected components via BFS/DFS:
   visited = empty HashSet
   components = new List<List<EdgeSlot>>
   For each unvisited node in graph:
     Walk all reachable edges from this node
     components.Add(this connected component)

4. If components.Count == 1:
   return GapScanResult { hasGap = false }

5. If components.Count > 1:
   outerComponent = component with largest Shoelace area
   innerComponents = all other components
   For each inner in innerComponents:
     area = ComputeLoopArea(inner)
     isClosed = inner has no degree-1 nodes (no dangling edges)
     If area > gapAreaThreshold AND isClosed:
       return GapScanResult { hasGap = true, gapArea = area }
     Else:
       fire WarnPlayer(NearlyEnclosedGap)

6. return GapScanResult { hasGap = false }
```

### 8.4 Shoelace Area Formula

```csharp
float ComputeLoopArea(List<EdgeSlot> loop) {
    // 1. Reconstruct ordered vertex list by walking edge adjacency
    // 2. Apply: area = 0.5 * abs( sum_i( x_i * y_{i+1} - x_{i+1} * y_i ) )
}
```

### 8.5 Edge Cases

**Pinch points** — A figure-8 cluster where two boundary sections meet at one vertex produces a degree-4 node. The BFS correctly splits into two components at this node, detecting the enclosed region.

**Nearly-enclosed gaps** — A pocket with one open edge remaining appears as a component with one degree-1 node. This is NOT game over. Warn the player but do not end the game.

**Endpoint deduplication** — Two OPEN slot endpoints within `0.005` world units must be merged during graph construction or they create false disconnections. The deduplication threshold must match the `EdgeKey` midpoint equality threshold from Section 4.3.

---

## 9. Integration Map

### 9.1 New Files

```
EdgeSlotRegistry.cs
  Pure C# class. No MonoBehaviour. No Unity physics dependencies.
  Fully unit-testable without scene context.
  API defined in Section 4.4.

GapDetector.cs
  Pure static utility class. No MonoBehaviour.
  Single public method: GapScanResult Scan(EdgeSlotRegistry registry)

SnapConfig.cs
  ScriptableObject. One asset per mode (ForgivingConfig.asset, StrictConfig.asset).
  Fields defined in Section 7.

SnapResult.cs
  Struct returned by TrySnap().
  Fields: bool success, Vector2 targetPos, Quaternion targetRot,
          List<EdgeSlot> closedSlots, RejectionReason reason
```

### 9.2 Modified Files

```
CoreCluster.cs
  ADD:  EdgeSlotRegistry  registry
  ADD:  Dictionary<Piece, Vector2[]>  canonicalVerts
  ADD:  SnapConfig  activeConfig
  ADD:  void BootstrapRegistry()
          Called in Start() after GridManager places initial pieces.
          Registers all initial cluster edges: inner edges CLOSED, outer edges OPEN.
  ADD:  void OnSnapConfirmed(Piece piece, SnapResult result)
          Stores canonical vertices, closes slots, runs GapDetector.Scan().
  KEEP: corePieces list, piece parenting, camera zoom (all unchanged)

GeometrySnapper.cs
  CHANGE:  TrySnap() accepts EdgeSlotRegistry and SnapConfig as parameters
  CHANGE:  GetWorldEdges() reads CanonicalVertexStore for snapped pieces,
           reads transform only for the currently-dragged piece
  REMOVE:  bodyOverlaps variable declared inside the vertex-building loop
  REMOVE:  the outward normal / CircleCast block (superseded by registry)
  ADD:     ComputeExpectedNeighbours() — Section 5.2
  ADD:     ConfirmNoBodyIntrusion() — Section 6.2
  ADD:     ComputeBestSnapRotation() — Forgiving mode auto-rotate, Section 7.1
  KEEP:    IsPointInPolygon() — unchanged
  KEEP:    Edge struct — unchanged

Piece.cs
  NO CHANGES required.
```

### 9.3 Unchanged Files

```
GameManager.cs, PieceSpawner.cs, InputManager.cs, GridManager.cs
```

---

## 10. Data Flow

### 10.1 Forgiving Mode

```
Player releases piece
        │
        ▼
InputManager.OnRelease(dragged)
        │
        ▼
GeometrySnapper.ComputeBestSnapRotation(dragged, registry, config)
  → Rotates dragged to nearest valid orientation before search
        │
        ▼
CoreCluster.TrySnapPiece(dragged, config)
        │
        ├──► registry.FindBestMatchingOpenSlot(best dragged edge, threshold)
        │         ├── null → no match, piece floats back
        │         └── EdgeSlot primarySlot
        │
        ├──► GeometrySnapper.ComputeCandidatePose(primarySlot, dragged)
        │         └── potentialPos, potentialRot
        │
        ├──► registry.FindAllMatchingOpenSlots(ALL projected dragged edges, threshold)
        │         └── matchedSlots → expectedNeighbours, sharedVertices
        │
        ├──► GeometrySnapper.ConfirmNoBodyIntrusion(
        │              potentialPose, dragged, expectedNeighbours, sharedVertices)
        │         ├── FAIL → try next candidate or return no-snap
        │         └── PASS
        │
        ├──► registry.CloseSlot(ALL slots in matchedSlots)   AUTO-CLOSE ALL
        ├──► registry.RegisterPieceEdges(dragged, canonicalVerts)
        ├──► canonicalVerts[dragged] = projected vertices
        ├──► Parent dragged to CoreCluster
        │
        ├──► GapDetector.Scan(registry)
        │         ├── Valid → continue
        │         └── Gap found → GameManager.TriggerGameOver()
        │
        └──► GameManager.OnSnapSuccess()
                  ├── PieceSpawner.SpawnNext()
                  └── CameraController.ZoomIfNeeded()
```

### 10.2 Strict Mode

```
Player releases piece  (at manually set rotation — no auto-rotate)
        │
        ▼
CoreCluster.TrySnapPiece(dragged, config)
        │
        ├──► registry.FindBestMatchingOpenSlot(best dragged edge, TIGHT threshold)
        │         ├── null → fire OnSnapRejected(NoOpenSlotNearby), float back
        │         └── EdgeSlot primarySlot
        │
        ├──► Check angular alignment against config.angularToleranceDegrees
        │         ├── Exceeds → fire OnSnapRejected(AngularMisalignment), float back
        │         └── Within → continue
        │
        ├──► GeometrySnapper.ComputeCandidatePose(primarySlot, dragged)
        │
        ├──► registry.FindAllMatchingOpenSlots(ALL projected edges, threshold)
        │         └── expectedNeighbours, sharedVertices
        │         NOTE: secondary matches identified but NOT auto-closed
        │
        ├──► GeometrySnapper.ConfirmNoBodyIntrusion(...)
        │         ├── FAIL → fire OnSnapRejected(BodyOverlap), float back
        │         └── PASS
        │
        ├──► registry.CloseSlot(primarySlot ONLY)   NO AUTO-CLOSE
        ├──► registry.RegisterPieceEdges(dragged, canonicalVerts)
        ├──► canonicalVerts[dragged] = projected vertices
        ├──► Parent dragged to CoreCluster
        │
        ├──► GapDetector.Scan(registry)
        │
        └──► GameManager.OnSnapSuccess()
```

---

## 11. Open Questions for the Agent

The agent must resolve these before writing code, either by asking the project owner or by inspecting existing scripts.

1. **Kite edge snappability**: Are all 4 edges of each kite snappable, or are certain edges restricted (e.g. the short spine edge is interior-only)? This determines which edges `RegisterPieceEdges` adds as OPEN.

2. **Edge length variety**: Do kites have two distinct edge lengths (short/long as in Penrose kites) or are all four edges the same length? This affects the weight given to the length component of `EdgeKey` matching.

3. **Starting cluster bootstrapping**: The initial cluster is placed by `GridManager`. Confirm that `CoreCluster.Start()` is the correct place for `BootstrapRegistry()`. The bootstrap must correctly identify which edges of the initial pieces are outer-facing (OPEN) versus shared between initial pieces (CLOSED).

4. **SnapConfig asset setup**: Should the agent create the two ScriptableObject preset assets (`ForgivingConfig.asset`, `StrictConfig.asset`) in the project, or will the project owner create them manually? What is the default mode at scene start?

5. **Pinch point policy**: If the cluster forms a figure-8 shape (boundary touching itself at one vertex), should this be a game over condition or allowed? Determines whether `GapDetector` flags degree-4 nodes.

6. **Gap area threshold**: What minimum enclosed area in world units triggers game over? Recommended default: 50% of a single kite tile's area, computed at bootstrap time from the initial piece geometry.

---

## 12. Implementation Order

Build in this sequence to allow incremental testing at each step:

1. `SnapConfig.cs` — ScriptableObject with both preset assets. No dependencies.
2. `EdgeSlotRegistry.cs` — Pure C#. Write unit tests before wiring into Unity.
3. `CanonicalVertexStore` — Add as Dictionary field to `CoreCluster`. Populate in existing snap callback.
4. Fix the broken vertex loop in `GeometrySnapper.TrySnap()` — isolated change, immediate improvement.
5. `BootstrapRegistry()` in `CoreCluster.Start()` — registers initial cluster edges correctly.
6. Wire `TrySnap()` to query the registry as primary filter, replacing the existing edge-length scan.
7. Add `ComputeExpectedNeighbours()` to `GeometrySnapper` — fixes closing piece rejection.
8. Update `ConfirmNoBodyIntrusion()` to skip expected neighbours and exclude shared vertices.
9. `GapDetector.cs` — Pure C#. Test with hand-constructed slot lists before wiring into Unity.
10. Wire `GapDetector.Scan()` through `CoreCluster.OnSnapConfirmed()`.
11. Add Strict mode `OnSnapRejected` events and `RejectionReason` enum.
12. Playtesting pass — tune `snapDistanceThreshold`, `angularToleranceDegrees`, gap area threshold.

---

*Document version 2. All architectural decisions finalised. Pass to agent with answers to Section 11.*

---

## 13. Hat Pattern Detection & Phase Transition

### 13.1 Overview

This feature is additive — it sits entirely on top of the existing snap system and does not modify `GeometrySnapper`, `EdgeSlotRegistry`, `GapDetector`, or any existing game logic. If the Hat pattern never forms, the kite game runs identically to without this feature. The risk of breakage to the current scene is zero.

The Hat tile is composed of exactly **8 kite pieces** in a specific shared-edge topology. The moment the 8th kite snaps and completes the Hat pattern, the system:

1. Destroys the 8 kite GameObjects
2. Instantiates a Hat prefab at the same world position and rotation
3. Registers the Hat prefab's edges into the `EdgeSlotRegistry` in place of the 8 kites
4. Switches `PieceSpawner` to spawn Hat prefabs instead of Kite prefabs
5. Switches `CoreCluster.activeConfig` to the Strict `SnapConfig`

### 13.2 The Hat Subgraph Definition

The Hat's topology must be encoded as a fixed reference graph. This graph describes which of the 8 kites share which edges — it is defined once as a static data structure and used by the pattern matcher at runtime.

```
HatSubgraph (static, defined once in HatPatternMatcher.cs)

Nodes: 8 kite slots, labelled K0 through K7
Edges: each entry is a pair (Ki, Kj) meaning Ki and Kj share a CLOSED edge

The agent must derive the exact adjacency from the Hat reference image:
  - Looking at Image 1, identify which of the 8 kites share an edge
  - Encode as: static readonly (int, int)[] HatAdjacency = { (0,1), (1,2), ... }

IMPORTANT: The graph is defined by topology only (which nodes connect to which),
not by position or rotation. The matcher is rotation-agnostic.
```

The agent's first task for this section is to study the Hat reference image and enumerate all shared edges between the 8 kites to produce the correct `HatAdjacency` array.

### 13.3 HatPatternMatcher.cs

New pure C# class. No MonoBehaviour. Called after every successful kite snap.

```
HatPatternMatcher
  
  static readonly (int,int)[]  HatAdjacency
    — The reference Hat subgraph topology (agent derives from image)

  HatMatchResult  TryFindHat(EdgeSlotRegistry registry, List<Piece> corePieces)
    — Entry point. Called from CoreCluster.OnSnapConfirmed() after every kite snap.
    — Returns HatMatchResult { bool found, List<Piece> matchedKites, Vector2 centroid, float rotation }

  private bool  TryMatchFromSeed(Piece seedPiece, EdgeSlotRegistry registry)
    — Attempts to match the Hat subgraph starting from seedPiece as K0
    — Uses BFS over CLOSED edges in the registry to find connected kite neighbours
    — Checks that the 8-piece connected component matches HatAdjacency topology
    — Returns true if full match found
```

**Algorithm — TryFindHat:**

```
1. For each piece P in corePieces (only newly snapped piece needed as seed,
   since Hat can only complete when the last of the 8 snaps):

2. Find all pieces reachable from P via CLOSED edges only (BFS, depth limit 7)
   — This gives the connected component containing P

3. If component size < 8 → no Hat possible from this seed → return not found

4. If component size >= 8 → extract all 8-piece subsets that include P
   (for a kite cluster this is usually just one candidate)

5. For each 8-piece candidate set:
   Build its adjacency graph from the registry's CLOSED edges
   Run graph isomorphism check against HatAdjacency
   If isomorphic → return HatMatchResult with these 8 pieces

6. Return not found
```

**Why only seed from the newly snapped piece:** A Hat pattern can only be completed at the moment its final (8th) piece snaps. So only subgraphs containing the newly snapped piece need to be checked. This keeps the check O(1) per snap rather than scanning the entire cluster.

### 13.4 Graph Isomorphism for 8 Nodes

Full general graph isomorphism is NP-hard but for a fixed 8-node reference graph it is trivially fast. The approach:

```
IsIsomorphic(candidateAdjacency, HatAdjacency):

1. Generate all permutations of [0..7] — maps candidate nodes to reference nodes
   (8! = 40320 permutations, each check is O(edges) — runs in microseconds)

2. For each permutation P:
   For each edge (Ki, Kj) in HatAdjacency:
     If (P[Ki], P[Kj]) not in candidateAdjacency → this permutation fails
   If all edges match → isomorphic, return true

3. Return false
```

In practice a degree-sequence pre-filter eliminates almost all permutations immediately (each node in the candidate must have the same number of CLOSED-edge neighbours as its mapped reference node), making this near-instant.

### 13.5 Phase Transition Sequence

Fires immediately when `TryFindHat` returns a match. Executed by `GameManager`.

```
GameManager.OnHatPatternDetected(HatMatchResult result):

1. FREEZE INPUT
   InputManager.SetEnabled(false)
   — Prevents player interaction during transition

2. DESTROY THE 8 KITES
   For each piece in result.matchedKites:
     registry.UnregisterPiece(piece)        — removes all its slots from registry
     canonicalVerts.Remove(piece)           — removes from vertex store
     corePieces.Remove(piece)              — removes from cluster list
     GameObject.Destroy(piece.gameObject)

3. INSTANTIATE HAT PREFAB
   Vector2 hatPosition = result.centroid    — centroid of the 8 kites' canonical vertices
   float   hatRotation = result.rotation    — rotation inferred from the matched subgraph orientation
   GameObject hatGO = Instantiate(hatPrefab, hatPosition, Quaternion.Euler(0,0,hatRotation))
   hatGO.transform.SetParent(CoreCluster.transform)
   Piece hatPiece = hatGO.GetComponent<Piece>()

4. REGISTER HAT INTO REGISTRY
   Vector2[] hatCanonicalVerts = ComputeHatCanonicalVerts(result)
   registry.RegisterPieceEdges(hatPiece, hatCanonicalVerts)
   canonicalVerts[hatPiece] = hatCanonicalVerts
   corePieces.Add(hatPiece)

5. SWITCH SNAP MODE
   CoreCluster.activeConfig = strictSnapConfig

6. SWITCH SPAWNER
   PieceSpawner.SetPiecePrefab(hatPrefab)

7. UNFREEZE INPUT
   InputManager.SetEnabled(true)

8. TRIGGER CELEBRATION (optional visual — does not block gameplay)
   UIManager.PlayHatTransitionEffect()
```

### 13.6 Computing Hat Canonical Vertices and Rotation

When the Hat prefab replaces the 8 kites, its canonical vertices must match the outer boundary of the 8 kites exactly. This is derived from the matched kites' canonical vertices — specifically, the vertices that were on OPEN edges (the outer boundary) form the Hat's polygon.

```
ComputeHatCanonicalVerts(HatMatchResult result):

1. Collect all OPEN edge endpoints from the 8 matched kites
   (read from registry — these are the outer boundary vertices)

2. Order them into a clockwise polygon by angle around the centroid

3. Return as Vector2[] — this is the Hat prefab's canonical vertex list

ComputeHatRotation(HatMatchResult result, int isomorphismMapping[]):

1. The isomorphism mapping tells us which candidate kite maps to K0 in the reference
2. K0's position relative to the centroid gives the reference orientation
3. Compare to the reference Hat's K0 offset direction → delta angle = hat rotation
```

### 13.7 New Files

```
HatPatternMatcher.cs
  — Pure C#. No MonoBehaviour. No Unity dependencies.
  — Fully unit-testable with mock registry data.
  — Key method: HatMatchResult TryFindHat(EdgeSlotRegistry, List<Piece>)

HatMatchResult.cs  (simple struct)
  — bool         found
  — List<Piece>  matchedKites
  — Vector2      centroid
  — float        rotation
  — int[]        isomorphismMapping   (which candidate piece maps to which reference node)
```

### 13.8 Additions to Existing Files

```
EdgeSlotRegistry.cs
  ADD: void UnregisterPiece(Piece piece)
       — Removes all slots owned by this piece from the dictionary
       — Used during Hat replacement to clean up the 8 kite entries

GameManager.cs
  ADD: void OnHatPatternDetected(HatMatchResult result)
       — Executes the full phase transition sequence from Section 13.5
  ADD: [SerializeField] GameObject hatPrefab
  ADD: [SerializeField] SnapConfig strictSnapConfig

CoreCluster.cs
  ADD: Call HatPatternMatcher.TryFindHat() inside OnSnapConfirmed(),
       after GapDetector.Scan() passes, passing the newly snapped piece as seed
  ADD: void ReplaceKitesWithHat(HatMatchResult result)
       — Delegates to GameManager.OnHatPatternDetected()
```

### 13.9 Test Scene Setup

A separate Unity scene — `HatTransitionTest` — for validating the detection and transition without the full game loop.

```
Scene contents:
  - CoreCluster with Forgiving SnapConfig
  - PieceSpawner configured for Kite prefabs only
  - HatPatternMatcher enabled
  - No gap detection game over (disabled for testing)
  - Debug overlay: shows current registry OPEN/CLOSED state
  - Debug overlay: highlights pieces that are part of a candidate Hat subgraph

Test procedure:
  1. Manually snap 7 kites into Hat formation
  2. Drop 8th kite → verify immediate transition fires
  3. Confirm Hat prefab appears at correct position and rotation
  4. Confirm registry now shows Hat's OPEN edges, not 8 kites' edges
  5. Confirm spawner now produces Hat prefabs
  6. Confirm activeConfig switched to Strict
```

### 13.10 Inspector Control Flag

The Hat detection is controlled by a single serialized bool on `CoreCluster`, visible and editable in the Unity Inspector at all times — no code changes needed to switch between modes.

```csharp
// On CoreCluster.cs
[Header("Hat Pattern Detection")]
[SerializeField] private bool enableHatDetection = false;
```

**When `enableHatDetection = false` (default):**
- `HatPatternMatcher.TryFindHat()` is never called
- The kite game runs as a pure infinite kite tiling game
- No phase transition, no Hat prefab, no mode switch
- Zero performance overhead
- This is the default for the `KiteGame` scene

**When `enableHatDetection = true`:**
- Hat detection runs after every successful snap
- Phase transition fires the moment the 8th kite completes the pattern
- Intended for the `HatTransitionTest` scene and eventually the combined game mode

This means you can maintain one `KiteGame` scene with the flag permanently off, and a separate `HatTransitionTest` scene with the flag on. Both use identical code — the flag is the only difference.

### 13.11 What Does NOT Change

```
GeometrySnapper.cs   — zero changes
EdgeSlotRegistry.cs  — one method added (UnregisterPiece), nothing modified
GapDetector.cs       — zero changes
Piece.cs             — zero changes
InputManager.cs      — zero changes (SetEnabled already exists or is trivial to add)
GridManager.cs       — zero changes
```

---

*Document version 2.1. Section 13 added. Hat pattern detection is non-breaking and additive.*
