using UnityEngine;

namespace TMKOC.Games.TilingGame
{
    public class GameManager : MonoBehaviour
    {
        public static GameManager Instance;
        
        [Header("References")]
        public PieceSpawner spawner;
        public CoreCluster coreCluster;
        
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
    }
}
