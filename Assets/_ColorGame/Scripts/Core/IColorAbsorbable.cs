using UnityEngine;

namespace ColorGame.Core
{
    /// <summary>
    /// Interface for entities that can absorb or be affected by color.
    /// Operates similarly to an IDamageable interface for combat.
    /// </summary>
    public interface IColorAbsorbable
    {
        void AbsorbColor(Color32 incomingColor);
    }
}
