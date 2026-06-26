#if TOOLS
using Godot;

namespace TopographicMap;

// Minimal editor plugin. Its presence makes the topographic addon enableable
// under Project Settings > Plugins. The effect itself is a [GlobalClass]
// CompositorEffect used directly on a map camera's Compositor.
[Tool]
public partial class TopographicPlugin : EditorPlugin
{
}
#endif
