using UnityEngine;
using System.Collections.Generic;

namespace TMKOC.Games.TilingGame
{
    // Attach to any GameObject, enter Play Mode, check Console output
    // Remove after verification — this is a temporary diagnostic only
    public class RegistryDebugBootstrap : MonoBehaviour
    {
        [SerializeField] private Piece pieceA;
        [SerializeField] private Piece pieceB;

        void Start()
        {
            if (pieceA == null || pieceB == null) return;

            var registry = new EdgeSlotRegistry();
            registry.BootstrapFromCluster(new List<Piece> { pieceA, pieceB });

            int open = registry.GetOpenSlots().Count;
            int total = registry.TotalSlotCount;
            int closed = total - open;

            Debug.Log($"[RegistryTest] Total slots: {total}, OPEN: {open}, CLOSED: {closed}");
            Debug.Log($"[RegistryTest] Expected for 2 adjacent kites: CLOSED >= 1, OPEN = total - closed");

            // Verify canonical vertices were stored
            Debug.Log($"[RegistryTest] Canonical vertex entries: {registry.canonicalVertices.Count} (expected 2)");
        }
    }
}
