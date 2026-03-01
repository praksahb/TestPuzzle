using UnityEngine;
using System.Collections.Generic;

namespace TMKOC.Games.TilingGame
{
    /// <summary>
    /// Immutable result struct returned by the snap pipeline.
    /// Contains everything CoreCluster needs to finalize or reject a snap.
    /// </summary>
    public struct SnapResult
    {
        public bool success;
        public Vector2 targetPos;
        public Quaternion targetRot;

        /// <summary>
        /// All registry slots that were matched (primary + adjacent).
        /// CoreCluster uses this list to close slots after confirmation.
        /// </summary>
        public List<EdgeSlotRegistry.EdgeSlot> closedSlots;

        /// <summary>
        /// Vertices at candidate pose, already computed and exact.
        /// Eliminates the need to wait for Unity transform syncs.
        /// </summary>
        public Vector2[] projectedVertices;

        /// <summary>
        /// If success is false, this explains why.
        /// Only populated when SnapConfig.emitRejectionEvents is true.
        /// </summary>
        public RejectionReason reason;
    }
}
