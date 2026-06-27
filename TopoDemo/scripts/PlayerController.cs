using Godot;

namespace TopographicMap.TopoDemo;

// Free-flying 6DOF hover-spaceship. No gravity and no collision: the node moves its own
// transform directly. Mouse motion accumulates into a target yaw and pitch that the actual
// orientation eases toward (smoothed look). The body only ever yaws on Y so MapUi can read
// GlobalRotation.Y as a clean heading; the camera pivot only pitches; the ship model tilts
// cosmetically.
public partial class PlayerController : Node3D
{
    [Export] public float MoveSpeed = 24.0f;
    [Export] public float BoostSpeed = 48.0f;
    [Export] public float VerticalSpeed = 20.0f;
    [Export] public float Acceleration = 6.0f; // higher = snappier stop/start
    [Export] public float MouseSensitivity = 0.0025f;
    [Export] public float LookSmoothing = 18.0f; // higher = look follows the mouse more tightly
    [Export] public float MinPitch = -1.4f;
    [Export] public float MaxPitch = 1.4f;

    [Export] public Node3D CameraPivot;
    [Export] public Node3D ShipModel;

    // Cosmetic only.
    [Export] public float BankAngle = 0.6f; // max roll into a strafe (radians)
    [Export] public float PitchTiltAngle = 0.3f; // max nose tilt from forward/vertical input
    [Export] public float ModelTiltSmoothing = 8.0f;

    private float _targetYaw;
    private float _targetPitch;
    private float _yaw;
    private float _pitch;
    private Vector3 _velocity;

    public override void _Ready()
    {
        // Start with the cursor free. Mouselook is enabled when the player clicks into the
        // window (see _UnhandledInput), so launching never confines the cursor.
        Input.MouseMode = Input.MouseModeEnum.Visible;

        _targetYaw = _yaw = Rotation.Y;
        _targetPitch = _pitch = CameraPivot.Rotation.X;
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        // Clicking into the window grabs the mouse and starts mouselook. While the world map
        // is open MapUi consumes its own left clicks, so this only fires during play.
        if (inputEvent is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }
            && Input.MouseMode == Input.MouseModeEnum.Visible)
        {
            Input.MouseMode = Input.MouseModeEnum.Captured;
            GetViewport().SetInputAsHandled();
            return;
        }

        if (inputEvent is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            // Accumulate into targets; the actual orientation eases toward them in _Process.
            _targetYaw -= motion.Relative.X * MouseSensitivity;
            _targetPitch = Mathf.Clamp(_targetPitch - motion.Relative.Y * MouseSensitivity, MinPitch, MaxPitch);
        }

        // Escape releases the mouse; click back into the window to grab it again.
        if (inputEvent.IsActionPressed("ui_cancel"))
        {
            Input.MouseMode = Input.MouseModeEnum.Visible;
        }
    }

    public override void _Process(double delta)
    {
        float dt = (float)delta;

        // Smooth look. The 1 - exp(-k*dt) factor makes the easing frame-rate independent.
        float lookT = 1.0f - Mathf.Exp(-LookSmoothing * dt);
        _yaw = Mathf.LerpAngle(_yaw, _targetYaw, lookT);
        _pitch = Mathf.Lerp(_pitch, _targetPitch, lookT);
        Rotation = new(0.0f, _yaw, 0.0f);
        CameraPivot.Rotation = new(_pitch, 0.0f, 0.0f);

        // Horizontal movement along the body facing (yaw only; pitch does not tilt movement).
        var input = Input.GetVector("move_left", "move_right", "move_forward", "move_back");
        var forward = new Vector3(-Mathf.Sin(_yaw), 0.0f, -Mathf.Cos(_yaw));
        var right = new Vector3(Mathf.Cos(_yaw), 0.0f, -Mathf.Sin(_yaw));
        // input.Y is positive for "back", so subtracting forward maps W to the facing direction.
        var horizontal = right * input.X - forward * input.Y;

        float vertical = 0.0f;
        if (Input.IsActionPressed("ascend")) vertical += 1.0f;
        if (Input.IsActionPressed("descend")) vertical -= 1.0f;

        float speed = Input.IsActionPressed("sprint") ? BoostSpeed : MoveSpeed;
        var targetVelocity = horizontal * speed + Vector3.Up * (vertical * VerticalSpeed);

        // Light damping toward the target velocity gives a hovercraft start/stop.
        float moveT = 1.0f - Mathf.Exp(-Acceleration * dt);
        _velocity = _velocity.Lerp(targetVelocity, moveT);
        GlobalPosition += _velocity * dt;

        UpdateModelTilt(input, vertical, dt);
    }

    // Cosmetic: roll the model into a strafe and tilt its nose with forward/vertical input.
    private void UpdateModelTilt(Vector2 input, float vertical, float dt)
    {
        float targetRoll = -input.X * BankAngle;
        float targetTilt = (input.Y - vertical) * 0.5f * PitchTiltAngle;
        float tiltT = 1.0f - Mathf.Exp(-ModelTiltSmoothing * dt);

        var rotation = ShipModel.Rotation;
        rotation.Z = Mathf.Lerp(rotation.Z, targetRoll, tiltT);
        rotation.X = Mathf.Lerp(rotation.X, targetTilt, tiltT);
        ShipModel.Rotation = rotation;
    }
}
