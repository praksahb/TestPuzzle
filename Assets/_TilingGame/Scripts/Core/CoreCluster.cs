using UnityEngine;
using System.Collections.Generic;

namespace TMKOC.Games.TilingGame
{
    public class CoreCluster : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private GridManager gridManager;
        [SerializeField] private Piece singleKitePrefab;

        [Header("Starting Settings")]
        [SerializeField, Tooltip("How many kites to pre-fill in the center (1 to 6).")]
        private int initialCenterKites = 6;

        [Header("Camera Settings")]
        [SerializeField] private float padding = 2f;
        [SerializeField] private float zoomSpeed = 3f;
        private float targetOrthoSize = 5f;
        private float minOrthoSize = 5f;

        [Header("Snap System (New)")]
        [SerializeField, Tooltip("Active snap configuration. Swap between Forgiving and Strict presets.")]
        private SnapConfig activeConfig;

        [SerializeField, Tooltip("Optional: Strict config to switch to when difficulty ramps up.")]
        private SnapConfig strictConfig;

        /// <summary>
        /// The Edge Slot Registry — the "brain" of the new snap system.
        /// Tracks every edge as OPEN or CLOSED. Replaces all physics-based occupancy checks.
        /// </summary>
        public EdgeSlotRegistry Registry { get; private set; }

        /// <summary>
        /// Event fired when a snap is rejected (Strict mode only).
        /// UI can subscribe to give the player feedback.
        /// </summary>
        public System.Action<RejectionReason> OnSnapRejected;

        /// <summary>
        /// Event fired when a gap is detected after a snap.
        /// </summary>
        public System.Action<GapDetector.GapScanResult> OnGapDetected;

        public static CoreCluster Instance { get; private set; }

        private void Awake()
        {
            if (Instance == null)
            {
                Instance = this;
            }
            else
            {
                Destroy(gameObject);
            }
        }

        private void Start()
        {
            if (Camera.main != null)
            {
                targetOrthoSize = Camera.main.orthographicSize;
                minOrthoSize = targetOrthoSize;
            }

            if (Application.isPlaying)
            {
                Registry = new EdgeSlotRegistry();
                InitializeCenterCluster();
                BootstrapRegistry();
            }
        }

        private void Update()
        {
            if (Camera.main != null && Mathf.Abs(Camera.main.orthographicSize - targetOrthoSize) > 0.01f)
            {
                Camera.main.orthographicSize = Mathf.Lerp(Camera.main.orthographicSize, targetOrthoSize, Time.deltaTime * zoomSpeed);
            }
        }

        // ─────────────────────────────────────────────
        //  Registry Bootstrap (Design Doc Section 4.4, Step 5)
        // ─────────────────────────────────────────────

        /// <summary>
        /// Reads all pieces currently parented to this CoreCluster and bootstraps
        /// the EdgeSlotRegistry. Shared edges → CLOSED, outer edges → OPEN.
        /// Called once in Start() after InitializeCenterCluster() has placed pieces.
        /// </summary>
        private void BootstrapRegistry()
        {
            List<Piece> initialPieces = new List<Piece>();
            foreach (Transform child in transform)
            {
                Piece p = child.GetComponent<Piece>();
                if (p != null) initialPieces.Add(p);
            }

            if (initialPieces.Count == 0)
            {
                Debug.LogWarning("[CoreCluster] No initial pieces found for registry bootstrap!");
                return;
            }

            Registry.BootstrapFromCluster(initialPieces);
        }

        // ─────────────────────────────────────────────
        //  Snap Pipeline (Design Doc Section 10.1 / 10.2)
        // ─────────────────────────────────────────────

        /// <summary>
        /// New unified snap entry point. Called by InputManager when the player releases a piece.
        /// Uses the EdgeSlotRegistry + GeometrySnapper pipeline.
        /// </summary>
        public bool TryPlacePiece(Piece piece)
        {
            if (Registry == null)
            {
                Debug.LogError("[CoreCluster] Registry is null! Was BootstrapRegistry() called?");
                return false;
            }

            // If the cluster is empty, place the first piece and bootstrap from it
            if (Registry.TotalSlotCount == 0)
            {
                piece.isFloating = false;
                piece.transform.SetParent(this.transform);

                // Register this first piece into the registry
                Vector2[] firstVerts = EdgeSlotRegistry.GetCurrentWorldVertices(piece);
                Registry.RegisterPieceEdges(piece, firstVerts);

                OnSnapSuccess(piece);
                return true;
            }

            // Get the active config (check for difficulty ramp)
            SnapConfig config = GetActiveConfig();

            // Forgiving mode: auto-rotate piece to nearest valid orientation
            if (config.autoRotate)
            {
                GeometrySnapper.ComputeBestSnapRotation(piece, Registry, config);
            }

            // Run the full snap pipeline
            SnapResult result = GeometrySnapper.TrySnap(piece, Registry, config);

            if (result.success)
            {
                // Move piece to the locked position
                piece.transform.position = result.targetPos;
                piece.transform.rotation = result.targetRot;
                piece.isFloating = false;
                piece.transform.SetParent(this.transform);

                // Store canonical vertices (frozen at snap time, never modified)
                Vector2[] canonicalVerts = EdgeSlotRegistry.GetCurrentWorldVertices(piece);
                Registry.RegisterPieceEdges(piece, canonicalVerts);

                // In Forgiving mode, all matched slots are auto-closed by RegisterPieceEdges.
                // In Strict mode, only close the primary slot (handled separately if needed).

                // Colorize on snap
                SpriteRenderer sr = piece.GetComponentInChildren<SpriteRenderer>();
                if (sr != null)
                {
                    sr.color = Random.ColorHSV(0f, 1f, 0.5f, 1f, 0.8f, 1f);
                }

                Debug.Log("[CoreCluster] Piece snapped via Edge Slot Registry!");

                // Run gap detection
                RunGapDetection();

                OnSnapSuccess(piece);
                return true;
            }
            else
            {
                // Snap failed
                if (config.emitRejectionEvents)
                {
                    OnSnapRejected?.Invoke(result.reason);
                    Debug.Log($"[CoreCluster] Snap rejected: {result.reason}");
                }
                return false;
            }
        }

        private void OnSnapSuccess(Piece piece)
        {
            // Tell GameManager to spawn next piece
            if (GameManager.Instance != null)
            {
                GameManager.Instance.OnPiecePlacedSuccessfully();
            }

            UpdateCameraBounds();
        }

        // ─────────────────────────────────────────────
        //  Gap Detection (Design Doc Section 8)
        // ─────────────────────────────────────────────

        private void RunGapDetection()
        {
            // Compute gap area threshold: 50% of a single kite tile's area
            // A rough estimate using the first piece's canonical vertices
            float gapAreaThreshold = 0.1f; // Safe default

            foreach (var kvp in Registry.canonicalVertices)
            {
                float area = ComputePolygonArea(kvp.Value);
                if (area > 0)
                {
                    gapAreaThreshold = area * 0.5f;
                    break;
                }
            }

            GapDetector.GapScanResult gapResult = GapDetector.Scan(Registry, gapAreaThreshold);

            if (gapResult.hasGap)
            {
                Debug.LogWarning($"[CoreCluster] GAP DETECTED! Area: {gapResult.gapArea:F3}");
                OnGapDetected?.Invoke(gapResult);
            }
            else if (gapResult.hasNearlyEnclosedGap)
            {
                Debug.Log("[CoreCluster] Nearly enclosed gap detected — warning player.");
            }
        }

        private float ComputePolygonArea(Vector2[] vertices)
        {
            if (vertices == null || vertices.Length < 3) return 0f;
            float area = 0f;
            for (int i = 0; i < vertices.Length; i++)
            {
                Vector2 current = vertices[i];
                Vector2 next = vertices[(i + 1) % vertices.Length];
                area += (current.x * next.y) - (next.x * current.y);
            }
            return Mathf.Abs(area) * 0.5f;
        }

        // ─────────────────────────────────────────────
        //  Difficulty Ramp (Design Doc Section 7)
        // ─────────────────────────────────────────────

        private SnapConfig GetActiveConfig()
        {
            if (activeConfig == null)
            {
                Debug.LogWarning("[CoreCluster] No SnapConfig assigned! Using defaults.");
                return ScriptableObject.CreateInstance<SnapConfig>();
            }

            // Check if we should switch to strict mode based on piece count
            if (strictConfig != null &&
                activeConfig.strictModeThreshold > 0 &&
                transform.childCount >= activeConfig.strictModeThreshold)
            {
                Debug.Log($"[CoreCluster] Difficulty ramp! Switching to Strict mode at {transform.childCount} pieces.");
                return strictConfig;
            }

            return activeConfig;
        }

        // ─────────────────────────────────────────────
        //  Camera Zoom (Unchanged)
        // ─────────────────────────────────────────────

        public void UpdateCameraBounds()
        {
            if (Camera.main == null) return;

            Bounds bounds = new Bounds(transform.position, Vector3.zero);
            bool hasBounds = false;

            foreach (Transform child in transform)
            {
                Renderer[] renderers = child.GetComponentsInChildren<Renderer>();
                foreach (Renderer r in renderers)
                {
                    if (!hasBounds)
                    {
                        bounds = r.bounds;
                        hasBounds = true;
                    }
                    else
                    {
                        bounds.Encapsulate(r.bounds);
                    }
                }
            }

            if (hasBounds)
            {
                float screenRatio = (float)Screen.width / (float)Screen.height;
                float targetSizeY = bounds.extents.y + padding;
                float targetSizeX = (bounds.extents.x + padding) / screenRatio;

                float newTargetSize = Mathf.Max(targetSizeX, targetSizeY);
                if (newTargetSize > targetOrthoSize)
                {
                    targetOrthoSize = Mathf.Max(minOrthoSize, newTargetSize);
                }
            }
        }

        // ─────────────────────────────────────────────
        //  GridManager-Based Initialization (Kept Intact)
        // ─────────────────────────────────────────────

        [ContextMenu("Generate Center Cluster")]
        private void InitializeCenterCluster()
        {
            if (gridManager == null || singleKitePrefab == null)
            {
                // GridManager is optional now. If not assigned, we expect manual placement.
                if (transform.childCount > 0)
                {
                    // Custom level design: lock existing children
                    foreach (Transform child in transform)
                    {
                        Piece p = child.GetComponent<Piece>();
                        if (p != null) p.isFloating = false;
                    }
                    UpdateCameraBounds();
                    return;
                }

                Debug.LogWarning("CoreCluster: No GridManager and no pre-placed pieces. " +
                                 "Place at least one Piece as a child of CoreCluster in the scene.");
                return;
            }

            // If the level designer manually placed pieces under CoreCluster in the Editor,
            // assume this is a custom level design and do not generate the giant spiral.
            if (transform.childCount > 0)
            {
                foreach (Transform child in transform)
                {
                    Piece p = child.GetComponent<Piece>();
                    if (p != null) p.isFloating = false;
                }
                UpdateCameraBounds();
                return;
            }

            ClearCenterCluster();

            // Generate an outward expanding spiral of coordinates
            int spawnedCount = 0;
            int radius = 0;

            while (spawnedCount < initialCenterKites)
            {
                for (int q = -radius; q <= radius; q++)
                {
                    int r1 = Mathf.Max(-radius, -q - radius);
                    int r2 = Mathf.Min(radius, -q + radius);

                    for (int r = r1; r <= r2; r++)
                    {
                        if (Mathf.Abs(q) != radius && Mathf.Abs(r) != radius && Mathf.Abs(q + r) != radius && radius != 0)
                            continue;

                        for (int k = 0; k < 6; k++)
                        {
                            if (spawnedCount >= initialCenterKites) break;

                            GridManager.KiteCoord coord = new GridManager.KiteCoord(q, r, k);
                            SpawnStaticKite(coord, k);
                            spawnedCount++;
                        }
                        if (spawnedCount >= initialCenterKites) break;
                    }
                    if (spawnedCount >= initialCenterKites) break;
                }
                radius++;
            }

            if (Application.isPlaying)
            {
                UpdateCameraBounds();
            }
        }

        private void SpawnStaticKite(GridManager.KiteCoord coord, int visualK)
        {
            if (Application.isPlaying)
            {
                gridManager.SetKiteFilled(coord, true);
            }

            Vector2 worldPos = gridManager.KiteToWorld(coord);
#if UNITY_EDITOR
            Piece staticKite = null;
            if (!Application.isPlaying)
            {
                staticKite = (Piece)UnityEditor.PrefabUtility.InstantiatePrefab(singleKitePrefab, this.transform);
                staticKite.transform.position = worldPos;
            }
            else
            {
                staticKite = Instantiate(singleKitePrefab, worldPos, Quaternion.identity, this.transform);
            }
#else
            Piece staticKite = Instantiate(singleKitePrefab, worldPos, Quaternion.identity, this.transform);
#endif

            staticKite.isFloating = false;
            staticKite.ForceSnapRotation(Quaternion.Euler(0, 0, visualK * 60f));
        }

        [ContextMenu("Clear Center Cluster")]
        private void ClearCenterCluster()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
                if (Application.isPlaying)
                    Destroy(transform.GetChild(i).gameObject);
                else
                    DestroyImmediate(transform.GetChild(i).gameObject);
            }
            if (!Application.isPlaying && gridManager != null)
            {
                gridManager.ClearGrid();
            }
        }
    }
}
