using UnityEngine;
using ColorGame.Core;

namespace ColorGame.Shooter
{
    [RequireComponent(typeof(Rigidbody2D))]
    public class ColorBall : MonoBehaviour
    {
        private Color32 _colorValue;
        private Rigidbody2D _rb;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
        }

        public void Initialize(Color32 subtractionData, Color visualColor, Vector2 velocity)
        {
            _colorValue = subtractionData;
            _rb.linearVelocity = velocity;

            var spriteRenderer = GetComponent<SpriteRenderer>();
            if (spriteRenderer != null)
            {
                spriteRenderer.color = visualColor;
            }
        }

        private void OnTriggerEnter2D(Collider2D other)
        {
            // Use the IColorAbsorbable interface instead of GetComponent on a specific class
            var absorbable = other.GetComponent<IColorAbsorbable>();
            if (absorbable != null)
            {
                absorbable.AbsorbColor(_colorValue);
                Destroy(gameObject); // Destroy ball upon collision with something that absorbs color
            }
        }

        private void OnBecameInvisible()
        {
            // Self-destruct when completely off-screen
            Destroy(gameObject);
        }
    }
}
