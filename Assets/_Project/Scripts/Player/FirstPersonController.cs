using UnityEngine;
using UnityEngine.InputSystem;

namespace ThePitt.Player
{
    /// <summary>First-person movement and mouse/gamepad look using the Player action map.</summary>
    [RequireComponent(typeof(CharacterController))]
    [DefaultExecutionOrder(-50)] // Before UI, so a UI closing on Esc gets the last word on the cursor.
    public class FirstPersonController : MonoBehaviour
    {
        [Header("References")]
        [SerializeField] private InputActionAsset actions;
        [SerializeField] private Transform cameraPivot;

        [Header("Movement")]
        [SerializeField, Min(0f)] private float walkSpeed = 4f;
        [SerializeField, Min(0f)] private float sprintSpeed = 7f;
        [SerializeField, Min(0f)] private float crouchSpeed = 2f;
        [SerializeField, Min(0f)] private float acceleration = 12f;
        [SerializeField, Min(0f)] private float jumpHeight = 1.1f;
        [SerializeField] private float gravity = -20f;

        [Header("Crouch")]
        [SerializeField, Min(0.5f)] private float standingHeight = 1.8f;
        [SerializeField, Min(0.5f)] private float crouchHeight = 1.1f;
        [SerializeField, Min(0f)] private float eyeOffsetFromTop = 0.15f;
        [SerializeField, Min(0f)] private float crouchTransitionSpeed = 10f;

        [Header("Look")]
        [Tooltip("Degrees per pixel of mouse movement.")]
        [SerializeField, Min(0f)] private float mouseSensitivity = 0.12f;
        [Tooltip("Degrees per second at full stick deflection.")]
        [SerializeField, Min(0f)] private float gamepadSensitivity = 180f;
        [SerializeField, Range(0f, 90f)] private float maxPitch = 85f;

        private CharacterController controller;
        private PlayerEffects effects;
        private InputAction move, look, jump, sprint, crouch;
        private Vector3 horizontalVelocity;
        private float verticalVelocity;
        private float pitch;
        private bool crouching;

        /// <summary>When false, movement and look are ignored (menus, dialogue).</summary>
        public bool InputEnabled { get; set; } = true;
        /// <summary>Multiplier on look speed from the options menu.</summary>
        public float SensitivityScale { get; set; } = 1f;
        /// <summary>Where the options menu keeps the look sensitivity multiplier.</summary>
        public const string SensitivityKey = "Options.Sensitivity";

        // Look input is dropped for a few frames after the view is set directly or the cursor locks: the first
        // locked frame can carry one huge jump of the mouse that would spin the player round.
        private int settleFrames;
        public float Pitch => pitch;
        public bool IsSprinting { get; private set; }
        public bool IsCrouching => crouching;

        private void Awake()
        {
            controller = GetComponent<CharacterController>();
            effects = GetComponent<PlayerEffects>();
            SensitivityScale = PlayerPrefs.GetFloat(SensitivityKey, 1f);
            if (cameraPivot == null && Camera.main != null) cameraPivot = Camera.main.transform;

            var map = actions.FindActionMap("Player", throwIfNotFound: true);
            move = map.FindAction("Move", true);
            look = map.FindAction("Look", true);
            jump = map.FindAction("Jump", true);
            sprint = map.FindAction("Sprint", true);
            crouch = map.FindAction("Crouch", true);

            controller.height = standingHeight;
            controller.center = new Vector3(0f, standingHeight / 2f, 0f);
            pitch = cameraPivot != null ? NormalizePitch(cameraPivot.localEulerAngles.x) : 0f;
        }

        private void OnEnable()
        {
            actions.FindActionMap("Player").Enable();
            LockCursor(true);
        }

        private void OnDisable()
        {
            actions.FindActionMap("Player").Disable();
            LockCursor(false);
        }

        private void Update()
        {
            HandleCursor();
            bool active = InputEnabled && Cursor.lockState == CursorLockMode.Locked;

            if (active) Look();
            Move(active);
            UpdateCrouch(active);
        }

        private void HandleCursor()
        {
            var keyboard = Keyboard.current;
            var mouse = Mouse.current;
            if (!InputEnabled) return;
            if (keyboard != null && keyboard.escapeKey.wasPressedThisFrame) LockCursor(false);
            else if (mouse != null && mouse.leftButton.wasPressedThisFrame
                     && Cursor.lockState != CursorLockMode.Locked)
            {
                LockCursor(true);
                settleFrames = 3;
            }
        }

        private static void LockCursor(bool locked)
        {
            Cursor.lockState = locked ? CursorLockMode.Locked : CursorLockMode.None;
            Cursor.visible = !locked;
        }

        private void Look()
        {
            Vector2 delta = look.ReadValue<Vector2>();
            if (settleFrames > 0)
            {
                settleFrames--;
                return;
            }
            bool fromPointer = look.activeControl?.device is Pointer;
            delta *= (fromPointer ? mouseSensitivity : gamepadSensitivity * Time.deltaTime) * SensitivityScale;

            transform.Rotate(0f, delta.x, 0f);
            pitch = Mathf.Clamp(pitch - delta.y, -maxPitch, maxPitch);
            if (cameraPivot != null) cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }

        private void Move(bool active)
        {
            Vector2 input = active ? Vector2.ClampMagnitude(move.ReadValue<Vector2>(), 1f) : Vector2.zero;
            IsSprinting = active && sprint.IsPressed() && !crouching && input.y > 0.1f;

            float speed = crouching ? crouchSpeed : IsSprinting ? sprintSpeed : walkSpeed;
            if (effects != null) speed *= effects.SpeedMultiplier;
            Vector3 target = (transform.right * input.x + transform.forward * input.y) * speed;
            horizontalVelocity = Vector3.MoveTowards(horizontalVelocity, target, acceleration * speed * Time.deltaTime);

            if (controller.isGrounded)
            {
                if (verticalVelocity < 0f) verticalVelocity = -2f;
                if (active && !crouching && jump.WasPressedThisFrame())
                    verticalVelocity = Mathf.Sqrt(jumpHeight * -2f * gravity);
            }
            verticalVelocity += gravity * Time.deltaTime;

            controller.Move((horizontalVelocity + Vector3.up * verticalVelocity) * Time.deltaTime);
        }

        private void UpdateCrouch(bool active)
        {
            bool wantCrouch = active && crouch.IsPressed();
            if (!wantCrouch && crouching && !CanStand()) wantCrouch = true;
            crouching = wantCrouch;

            float targetHeight = crouching ? crouchHeight : standingHeight;
            float height = Mathf.MoveTowards(controller.height, targetHeight, crouchTransitionSpeed * Time.deltaTime);
            controller.height = height;
            controller.center = new Vector3(0f, height / 2f, 0f);

            if (cameraPivot != null)
                cameraPivot.localPosition = new Vector3(0f, height - eyeOffsetFromTop, 0f);
        }

        private bool CanStand()
        {
            // Casts ignore colliders they start inside, so our own capsule is not hit.
            float radius = controller.radius * 0.95f;
            Vector3 origin = transform.position + Vector3.up * (controller.height - radius);
            float distance = standingHeight - controller.height;
            return !Physics.SphereCast(origin, radius, Vector3.up, out _, distance, ~0, QueryTriggerInteraction.Ignore);
        }

        private static float NormalizePitch(float x) => x > 180f ? x - 360f : x;

        /// <summary>Faces a direction: yaw turns the body, pitch tilts the camera (loading a save).</summary>
        public void SetLook(float yaw, float newPitch)
        {
            transform.rotation = Quaternion.Euler(0f, yaw, 0f);
            pitch = Mathf.Clamp(newPitch, -maxPitch, maxPitch);
            settleFrames = 3;
            if (cameraPivot != null) cameraPivot.localRotation = Quaternion.Euler(pitch, 0f, 0f);
        }
    }
}
