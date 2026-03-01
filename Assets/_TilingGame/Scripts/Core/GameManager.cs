using UnityEngine;

namespace TMKOC.Games.TilingGame
{
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance;
        
        [Header("References")]
        [SerializeField] private PieceSpawner spawner;
        [SerializeField] private CoreCluster coreCluster;
        
        private void Awake()
        {
            if (Instance == null) Instance = this;
            else Destroy(gameObject);
        }

        private void Start()
        {
            // Start the game by spawning the first piece
            SpawnNextPiece();
        }

        public void OnPiecePlacedSuccessfully()
        {
            // Called by CoreCluster when a piece perfectly snaps into a valid spot on the grid
            Debug.Log("GameManager: Piece placed successfully! Spawning next...");
            SpawnNextPiece();
        }

        private void SpawnNextPiece()
        {
            if (spawner != null)
            {
                spawner.SpawnNewPiece();
            }
        }

        public void TriggerGameOver()
        {
            Debug.Log("GameManager: GAME OVER triggered (Enclosed Gap Detected!)");
            // Future UI/Game State Implementation
        }

        [Header("Hat Settings")]
        [SerializeField] private Piece hatPrefab;

        public void OnHatDetected(HatMatchResult result)
        {
            Debug.Log($"[Hat Spawn] centroid=({result.centroid.x:F4},{result.centroid.y:F4}) rotation={result.rotation:F4}");
            Debug.Log($"[Hat Spawn] Kite positions and rotations:");
            foreach (Piece p in result.matchedKites)
                Debug.Log($"[Hat Spawn]   {p.name}: pos=({p.transform.position.x:F3},{p.transform.position.y:F3}) rot={p.transform.eulerAngles.z:F1}");

            // 1. Disable input temporarily
            InputManager.Instance.SetEnabled(false);

            // 2. Destroy the 8 matched kites
            // DEBUG: temporarily keep kites visible to verify Hat alignment
            // foreach (Piece piece in result.matchedKites)
            // {
            //     CoreCluster.Instance.Registry.UnregisterPiece(piece);
            //     CoreCluster.Instance.RemoveFromCorePieces(piece);
            //     Destroy(piece.gameObject);
            // }

            // 3. Instantiate Hat prefab at correct position and rotation
            float coreRotation = CoreCluster.Instance.GetCorePieceRotation(); // center piece Z rotation
            float spawnRotation = coreRotation + 30f;

            Piece hatGO = Instantiate(
                hatPrefab,
                new Vector3(result.centroid.x, result.centroid.y, 0f),
                Quaternion.Euler(0f, 0f, spawnRotation)
            );
            hatGO.transform.SetParent(CoreCluster.Instance.transform);

            // 4. Register Hat edges
            Physics2D.SyncTransforms();
            Vector2[] hatVerts = EdgeSlotRegistry.GetCurrentWorldVertices(hatGO);
            CoreCluster.Instance.Registry.RegisterPieceEdges(hatGO, hatVerts);
            CoreCluster.Instance.AddToCorePieces(hatGO);

            // 5. Upgrade the PieceSpawner to strictly spawn Hats from now on!
            spawner.piecePrefab = hatPrefab;

            // 6. Re-enable input and switch to strict mode
            InputManager.Instance.SetEnabled(true);
            CoreCluster.Instance.SwitchToStrictConfig();

            Debug.Log($"[GameManager] Hat spawned at {result.centroid} rotation {result.rotation}");
        }
    }
}
