using Godot;

namespace TopographicMap.TopoDemo;

public partial class PlayerController : CharacterBody3D
{
    [Export] public float MoveSpeed = 16.0f;
    [Export] public float SprintSpeed = 30.0f;
    [Export] public float JumpVelocity = 8.0f;
    [Export] public float MouseSensitivity = 0.002f;
    [Export] public float MinPitch = -1.2f;
    [Export] public float MaxPitch = 0.4f;

    [Export] public Node3D CameraPivot;
    [Export] public Node3D Body;

    private float _gravity = (float)ProjectSettings.GetSetting("physics/3d/default_gravity");
    private float _pitch;

    public override void _Ready()
    {
        Input.MouseMode = Input.MouseModeEnum.Captured;

        // Keep the third-person spring arm from colliding with the player's own body.
        if (CameraPivot is SpringArm3D springArm)
        {
            springArm.AddExcludedObject(GetRid());
        }
    }

    public override void _UnhandledInput(InputEvent inputEvent)
    {
        if (inputEvent is InputEventMouseMotion motion && Input.MouseMode == Input.MouseModeEnum.Captured)
        {
            CameraPivot.RotateY(-motion.Relative.X * MouseSensitivity);
            _pitch = Mathf.Clamp(_pitch - motion.Relative.Y * MouseSensitivity, MinPitch, MaxPitch);
            CameraPivot.Rotation = new(_pitch, CameraPivot.Rotation.Y, 0.0f);
        }

        if (inputEvent.IsActionPressed("ui_cancel"))
        {
            Input.MouseMode = Input.MouseMode == Input.MouseModeEnum.Captured
                ? Input.MouseModeEnum.Visible
                : Input.MouseModeEnum.Captured;
        }
    }

    public override void _PhysicsProcess(double delta)
    {
        var velocity = Velocity;

        if (!IsOnFloor())
        {
            velocity.Y -= _gravity * (float)delta;
        }
        else if (Input.IsActionJustPressed("jump"))
        {
            velocity.Y = JumpVelocity;
        }

        var input = Input.GetVector("move_left", "move_right", "move_forward", "move_back");
        float yaw = CameraPivot.Rotation.Y;
        var forward = new Vector3(-Mathf.Sin(yaw), 0.0f, -Mathf.Cos(yaw));
        var right = new Vector3(Mathf.Cos(yaw), 0.0f, -Mathf.Sin(yaw));
        // input.Y is positive for "back", so subtracting forward maps W to camera forward.
        var direction = (right * input.X - forward * input.Y).Normalized();

        // Face the camera direction so the body and its parented map marker show heading.
        Body.Rotation = new(0.0f, yaw, 0.0f);

        float speed = Input.IsActionPressed("sprint") ? SprintSpeed : MoveSpeed;
        if (direction.LengthSquared() > 0.001f)
        {
            velocity.X = direction.X * speed;
            velocity.Z = direction.Z * speed;
        }
        else
        {
            velocity.X = Mathf.MoveToward(velocity.X, 0.0f, speed);
            velocity.Z = Mathf.MoveToward(velocity.Z, 0.0f, speed);
        }

        Velocity = velocity;
        MoveAndSlide();
    }
}
