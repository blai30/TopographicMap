using Godot;

namespace TopographicCameraShader.Demo;

// First-person character controller. WASD relative to look direction, mouse
// look with a captured cursor, gravity and a jump. Look and horizontal
// movement are suppressed while InputEnabled is false (e.g. world map open).
public partial class PlayerController : CharacterBody3D
{
    [Export] public float WalkSpeed { get; set; } = 5f;
    [Export] public float SprintSpeed { get; set; } = 8f;
    [Export] public float JumpVelocity { get; set; } = 4.5f;
    [Export] public float MouseSensitivity { get; set; } = 0.0025f;
    [Export] public Camera3D Camera { get; set; }

    public bool InputEnabled { get; set; } = true;

    private float _pitch;
    private float _gravity = 9.8f;

    public override void _Ready()
    {
        _gravity = ProjectSettings.GetSetting("physics/3d/default_gravity", 9.8f).AsSingle();
        Input.MouseMode = Input.MouseModeEnum.Captured;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey { Pressed: true, Echo: false, Keycode: Key.Escape })
        {
            // Dev convenience: release / recapture the cursor.
            Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                ? Input.MouseModeEnum.Visible
                : Input.MouseModeEnum.Captured;
            return;
        }

        if (!InputEnabled) return;
        if (@event is not InputEventMouseMotion motion || Input.MouseMode != Input.MouseModeEnum.Captured) return;
        RotateY(-motion.Relative.X * MouseSensitivity);
        _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity, -1.55f, 1.55f);
        Camera.Rotation = new(_pitch, 0f, 0f);
    }

    public override void _PhysicsProcess(double delta)
    {
        var velocity = Velocity;

        if (!IsOnFloor())
            velocity.Y -= _gravity * (float)delta;

        if (InputEnabled)
        {
            if (Input.IsKeyPressed(Key.Space) && IsOnFloor())
                velocity.Y = JumpVelocity;

            var input = Vector2.Zero;
            if (Input.IsKeyPressed(Key.W)) input.Y -= 1f;
            if (Input.IsKeyPressed(Key.S)) input.Y += 1f;
            if (Input.IsKeyPressed(Key.A)) input.X -= 1f;
            if (Input.IsKeyPressed(Key.D)) input.X += 1f;

            float speed = Input.IsKeyPressed(Key.Shift) ? SprintSpeed : WalkSpeed;
            var direction = (Transform.Basis * new Vector3(input.X, 0f, input.Y)).Normalized();
            velocity.X = direction.X * speed;
            velocity.Z = direction.Z * speed;
        }
        else
        {
            velocity.X = 0f;
            velocity.Z = 0f;
        }

        Velocity = velocity;
        MoveAndSlide();
    }
}
