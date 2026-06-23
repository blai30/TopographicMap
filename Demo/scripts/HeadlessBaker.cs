using Godot;

namespace TopographicCameraShader.Demo;

// Temporary headless bake entry point. Lets the terrain be re-baked from the
// command line (godot --headless res://scenes/HeadlessBake.tscn) so the editor
// does not have to reload the [Tool] assembly. Bakes once on ready, then quits.
public partial class HeadlessBaker : Node
{
    public override void _Ready()
    {
        GD.Print("HeadlessBaker: starting bake...");
        TerrainBaker.BakeToFiles();
        GD.Print("HeadlessBaker: done.");
        GetTree().Quit();
    }
}
