using UnityEngine;

namespace ColorGame.Player
{
    [RequireComponent(typeof(Rigidbody2D))]
    public class PlayerController : MonoBehaviour
    {
        [SerializeField] private float _moveSpeed = 5f;
        
        private Rigidbody2D _rb;
        private Vector2 _movement;

        private void Awake()
        {
            _rb = GetComponent<Rigidbody2D>();
        }

        // This method will be called by the PlayerInput component via Message or UnityEvent
        public void OnMove(UnityEngine.InputSystem.InputValue value)
        {
            _movement = value.Get<Vector2>();
        }

        private void FixedUpdate()
        {
            // Apply movement
            _rb.linearVelocity = _movement * _moveSpeed;
        }
    }
}
