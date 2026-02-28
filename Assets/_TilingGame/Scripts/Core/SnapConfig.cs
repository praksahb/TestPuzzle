using UnityEngine;

namespace TMKOC.Games.TilingGame
{
    public enum SnapMode { Forgiving, Strict }

    public enum RejectionReason
    {
        NoOpenSlotNearby,
        EdgeLengthMismatch,
        AngularMisalignment,
        BodyOverlap,
        SlotOccupied
    }

    [CreateAssetMenu(fileName = "NewSnapConfig", menuName = "TilingGame/Snap Config")]
    public class SnapConfig : ScriptableObject
    {
        [Header("Mode")]
        public SnapMode mode = SnapMode.Forgiving;

        [Header("Snap Thresholds")]
        [Tooltip("Maximum world-space distance between edge midpoints to consider a snap candidate.")]
        public float snapDistanceThreshold = 0.6f;

        [Tooltip("Maximum angular difference (degrees) between a dragged edge and a slot edge to accept a snap.")]
        public float angularToleranceDegrees = 25f;

        [Header("Automation")]
        [Tooltip("If true, the system auto-rotates the dragged piece to the nearest valid orientation before snapping.")]
        public bool autoRotate = true;

        [Tooltip("If true, all edges of the incoming piece that align with open slots are closed automatically on snap. If false, only the primary matched edge is closed.")]
        public bool autoCloseAdjacentEdges = true;

        [Header("Feedback")]
        [Tooltip("If true, rejected snaps fire OnSnapRejected events with a specific reason.")]
        public bool emitRejectionEvents = false;

        [Header("Difficulty Ramp (Kite Game)")]
        [Tooltip("Number of pieces in the cluster at which the system switches to Strict mode. 0 = never switch.")]
        public int strictModeThreshold = 0;
    }
}
