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

        [Header("Hat Pattern Detection")]
        [SerializeField] private bool enableHatDetection = false;

        public EdgeSlotRegistry Registry { get; private set; }

        public System.Action<RejectionReason> OnSnapRejected;

        public static CoreCluster Instance { get; private set; }

        public List<Piece> corePieces = new List<Piece>();

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

        private void BootstrapRegistry()
        {
            Physics2D.SyncTransforms(); // ensure all socket positions are current before reading
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

            corePieces.AddRange(initialPieces);
            Registry.BootstrapFromCluster(initialPieces);
        }

        public bool TryPlacePiece(Piece piece)
        {
            if (Registry == null)
            {
                Debug.LogError("[CoreCluster] Registry is null! Was BootstrapRegistry() called?");
                return false;
            }

            float zAngle = piece.transform.eulerAngles.z;
            float snappedZ = Mathf.Round(zAngle / 30f) * 30f;
            piece.transform.rotation = Quaternion.Euler(0f, 0f, snappedZ);
            Physics2D.SyncTransforms(); // force child sockets to update before GetWorldEdges reads them

            if (Registry.TotalSlotCount == 0)
            {
                // Fix #1 order for the very first piece
                Vector2[] firstVerts = EdgeSlotRegistry.GetCurrentWorldVertices(piece); // BEFORE SetParent
                AddToCorePieces(piece);
                Registry.RegisterPieceEdges(piece, firstVerts);

                OnSnapSuccess(piece);
                return true;
            }

            SnapConfig config = GetActiveConfig();

            if (config.autoRotate)
            {
                GeometrySnapper.ComputeBestSnapRotation(piece, Registry, config); 
            }

            SnapResult result = GeometrySnapper.TrySnap(piece, Registry, config);

            if (result.success)
            {
                // Fix #1: Apply pose, disable float, set parent, register exact computed edges. EXACT order.
                piece.transform.position = result.targetPos;
                piece.transform.rotation = result.targetRot;
                
                // Use exact vertices computed from the successful snap pose (prevents Unity transform sync delay issues)
                Vector2[] canonicalVerts = result.projectedVertices;
                
                AddToCorePieces(piece);
                
                Registry.RegisterPieceEdges(piece, canonicalVerts); // pass already-captured exact verts

                // TODO: Strict mode extra slot-closure handling logic to be added or determined in integration

                SpriteRenderer sr = piece.GetComponentInChildren<SpriteRenderer>();
                if (sr != null)
                {
                    sr.color = Random.ColorHSV(0f, 1f, 0.5f, 1f, 0.8f, 1f);
                }

                OnSnapSuccess(piece);
                return true;
            }
            else
            {
                // Fix #4: Always log the rejection reason, but only fire event if flag is true
                // Debug.Log($"[CoreCluster] Snap rejected: {result.reason}");
                if (config.emitRejectionEvents)
                {
                    OnSnapRejected?.Invoke(result.reason);
                }
                return false;
            }
        }

        private void OnSnapSuccess(Piece piece)
        {
            // 1. RegisterPieceEdges is already done in TryPlacePiece before calling this.
            
            // 2. Gap detector
            GapDetector.GapScanResult gapResult = GapDetector.Scan(Registry, 0.1f);
            if (gapResult.hasGap) 
            { 
                 if (GameManager.Instance != null)
                 {
                     GameManager.Instance.TriggerGameOver(); 
                 }
            }

            // 3. Hat pattern matcher
            if (enableHatDetection)
            {
                HatMatchResult hatResult = HatPatternMatcher.TryFindHat(Registry, corePieces, piece);
                 if (hatResult.success)
                 {
                      Debug.Log($"[CoreCluster] HIT! HAT PATTERN DETECTED around {piece.name}");
                      if (GameManager.Instance != null) { GameManager.Instance.OnHatDetected(hatResult); }
                 }
            }

            // 4. On piece placed
            if (GameManager.Instance != null)
            {
                GameManager.Instance.OnPiecePlacedSuccessfully();
            }

            // 5. Update Bounds
            UpdateCameraBounds();
        }

        private SnapConfig GetActiveConfig()
        {
            if (activeConfig == null)
            {
                return ScriptableObject.CreateInstance<SnapConfig>();
            }

            if (strictConfig != null &&
                activeConfig.strictModeThreshold > 0 &&
                transform.childCount >= activeConfig.strictModeThreshold)
            {
                return strictConfig;
            }

            return activeConfig;
        }

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

        [ContextMenu("Generate Center Cluster")]
        private void InitializeCenterCluster()
        {
            if (gridManager == null || singleKitePrefab == null)
            {
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
                return;
            }

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
        public void SwitchToStrictConfig()
        {
            if (strictConfig != null)
            {
                activeConfig = strictConfig;
                Debug.Log("[CoreCluster] Switched to Strict Configuration.");
            }
        }

        public void RemoveFromCorePieces(Piece piece)
        {
            if (corePieces.Contains(piece))
            {
                corePieces.Remove(piece);
            }
        }

        public void AddToCorePieces(Piece piece)
        {
            piece.transform.SetParent(this.transform);
            piece.isFloating = false;
            if (!corePieces.Contains(piece))
            {
                corePieces.Add(piece);
            }
        }

        public float GetCorePieceRotation()
        {
            foreach (Transform child in transform)
            {
                Piece p = child.GetComponent<Piece>();
                if (p != null && p.name == "Kite_1_PerfectCollider") // non-clone, original
                {
                    Debug.Log($"[CorePiece] Found original: {p.name} rot={p.transform.eulerAngles.z}");
                    return p.transform.eulerAngles.z;
                }
            }
            Debug.Log("[CorePiece] Fallback to corePieces[0]");
            return corePieces.Count > 0 ? corePieces[0].transform.eulerAngles.z : 0f;
        }
    }
}
