using System;
using Godot;

namespace TopographicMap;

// Compositor effect that turns the top-down camera depth buffer into topographic data via
// compute shaders. It writes R = normalized world height and G = coverage mask into the
// RGBA16F color image, and, in a separate persistent texture, the per-cell contour SEGMENT
// (both endpoints, in UV) crossing each grid cell. The unified canvas shader samples that
// segment texture and computes exact point-to-segment distance per display pixel, so it
// draws crisp constant-width vector lines with no distance field, no CPU work, and no bake.
// Attach via a Compositor on the map camera only.
[Tool]
[GlobalClass]
public partial class TopographicCompositorEffect : CompositorEffect
{
    [Export] public float HeightMin = -40.0f;
    [Export] public float HeightMax = 110.0f;
    [Export] public float ContourInterval = 10.0f;
    [Export] public float CameraY = 200.0f;
    [Export] public float NearPlane = 80.0f;
    [Export] public float FarPlane = 245.0f;
    [Export] public bool DepthReversed = true;

    // Optional pre-seed blur of the height buffer, in buffer texels. 0 = off (no blur); the
    // default 4 smooths rough/high-frequency terrain, and higher values give smoother, flowing
    // contours. Smooths the tint bands and the contour lines together since both read the same buffer.
    [Export(PropertyHint.Range, "0,8,1,prefer_slider")]
    public int ContourSmoothness = 4;

    // Persistent per-cell contour segment texture, wrapped as a Texture2D so the canvas
    // shader can sample it directly and compute exact (vector) line distance per display
    // pixel. The compositor owns the underlying RD texture and sets this wrapper's RID.
    public Texture2Drd SegmentTexture { get; } = new();

    // True once the first render callback has produced the real segment texture. Consumers
    // should wait for this before drawing, so they never sample before the producer runs
    // (drawing the segment sampler before its RID is live trips a "set (1)" draw error). Set
    // on the rendering thread, read on the main thread, hence volatile.
    private volatile bool _hasProduced;
    public bool HasProduced => _hasProduced;

    private RenderingDevice _renderingDevice;
    private Rid _heightShader, _heightPipeline;
    private Rid _seedShader, _seedPipeline;
    private Rid _blurShader, _blurPipeline;
    private Rid _depthSampler;
    private Rid _segments;
    private Vector2I _segmentSize;
    private Vector2I _maxSize;
    private Rid _blurTemp;
    private Vector2I _blurTempSize;
    private bool _ready;

    public TopographicCompositorEffect()
    {
        EffectCallbackType = EffectCallbackTypeEnum.PreTransparent;
        _renderingDevice = RenderingServer.GetRenderingDevice();
        if (_renderingDevice == null) return;

        bool ok = LoadShader("res://addons/topographic/depth_to_height.glsl", out _heightShader, out _heightPipeline) &&
                  LoadShader("res://addons/topographic/contour_seed.glsl", out _seedShader, out _seedPipeline) &&
                  LoadShader("res://addons/topographic/height_blur.glsl", out _blurShader, out _blurPipeline);
        if (!ok) return;

        var nearest = new RDSamplerState
        {
            MinFilter = RenderingDevice.SamplerFilter.Nearest,
            MagFilter = RenderingDevice.SamplerFilter.Nearest,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge
        };
        _depthSampler = _renderingDevice.SamplerCreate(nearest);

        // Give SegmentTexture a valid RID up front. A consumer view binds this wrapper at
        // _Ready, before the first render populates the real segment texture; an empty RID
        // would make the canvas shader fail to supply its sampler uniform on that first draw
        // ("Uniforms were never supplied for set (1)"). A 1x1 placeholder avoids that window
        // and is replaced at the real buffer size on the first render callback.
        CreateSegmentTexture(new(1, 1));
        _ready = _depthSampler.IsValid;
    }

    private bool LoadShader(string path, out Rid shader, out Rid pipeline)
    {
        shader = new();
        pipeline = new();
        var shaderFile = GD.Load<RDShaderFile>(path);
        if (shaderFile == null) return false;

        shader = _renderingDevice.ShaderCreateFromSpirV(shaderFile.GetSpirV());
        if (!shader.IsValid) return false;

        pipeline = _renderingDevice.ComputePipelineCreate(shader);
        return pipeline.IsValid;
    }

    public override void _Notification(int what)
    {
        if (what != NotificationPredelete || _renderingDevice == null) return;

        // Clear the wrapper's RID before the underlying RD texture is freed, or the setter would
        // operate on a freed RID.
        SegmentTexture.TextureRdRid = new();

        // RenderingDevice.FreeRid must run on the render thread, but predelete runs on the main
        // thread; freeing directly here errors on mid-session teardown (editor scene switch or C#
        // recompile). Defer to the render thread, capturing locals so the callback never touches
        // this dying object.
        var renderingDevice = _renderingDevice;
        Rid[] rids =
        [
            _depthSampler, _segments, _blurTemp, _heightPipeline, _heightShader,
            _seedPipeline, _seedShader, _blurPipeline, _blurShader
        ];
        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            foreach (var rid in rids)
            {
                if (rid.IsValid) renderingDevice.FreeRid(rid);
            }
        }));
    }

    private void FreeRid(Rid rid)
    {
        if (rid.IsValid) _renderingDevice.FreeRid(rid);
    }

    private void EnsureSegmentTexture(Vector2I size)
    {
        if (_segments.IsValid && _segmentSize == size) return;
        CreateSegmentTexture(size);
    }

    private void CreateSegmentTexture(Vector2I size)
    {
        // StorageBit: the seed pass writes it. SamplingBit: the canvas shader samples it.
        // CanCopyFromBit: the editor inspector reads it back to thumbnail the bound texture
        // parameter, which errors without copy-from permission.
        var format = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R32G32B32A32Sfloat,
            Width = (uint)size.X,
            Height = (uint)size.Y,
            Depth = 1,
            ArrayLayers = 1,
            Mipmaps = 1,
            TextureType = RenderingDevice.TextureType.Type2D,
            UsageBits = RenderingDevice.TextureUsageBits.StorageBit | RenderingDevice.TextureUsageBits.SamplingBit |
                        RenderingDevice.TextureUsageBits.CanUpdateBit | RenderingDevice.TextureUsageBits.CanCopyFromBit
        };

        // Repoint the Texture2Drd to the new texture BEFORE freeing the previous rd_texture.
        // The wrapper still references the old RID, so freeing it first would leave the
        // setter operating on a freed RID ("Attempted to free invalid ID" / double free).
        var previous = _segments;
        _segments = _renderingDevice.TextureCreate(format, new(), []);
        SegmentTexture.TextureRdRid = _segments;
        FreeRid(previous);
        _segmentSize = size;
    }

    private void EnsureBlurTemp(Vector2I size)
    {
        if (_blurTemp.IsValid && _blurTempSize == size) return;

        // Scratch RGBA16F target for the horizontal blur pass, matching the color image's
        // format. StorageBit only: the compute passes read and write it, nothing samples it.
        var format = new RDTextureFormat
        {
            Format = RenderingDevice.DataFormat.R16G16B16A16Sfloat,
            Width = (uint)size.X,
            Height = (uint)size.Y,
            Depth = 1,
            ArrayLayers = 1,
            Mipmaps = 1,
            TextureType = RenderingDevice.TextureType.Type2D,
            UsageBits = RenderingDevice.TextureUsageBits.StorageBit
        };

        // Same set-before-free ordering as CreateSegmentTexture, to avoid a double free.
        var previous = _blurTemp;
        _blurTemp = _renderingDevice.TextureCreate(format, new(), []);
        FreeRid(previous);
        _blurTempSize = size;
    }

    private static RDUniform ImageUniform(int binding, Rid image)
    {
        var uniform = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = binding };
        uniform.AddId(image);
        return uniform;
    }

    private static RDUniform SamplerUniform(int binding, Rid sampler, Rid texture)
    {
        var uniform = new RDUniform { UniformType = RenderingDevice.UniformType.SamplerWithTexture, Binding = binding };
        uniform.AddId(sampler);
        uniform.AddId(texture);
        return uniform;
    }

    private static byte[] Floats(params float[] values)
    {
        byte[] bytes = new byte[values.Length * sizeof(float)];
        Buffer.BlockCopy(values, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    // Push constant for height_blur.glsl: vec2 size (floats) + ivec2 dir + int radius + padding,
    // packed to 32 bytes (a multiple of 16, as Godot push constants require).
    private static byte[] BlurPush(Vector2I size, Vector2I dir, int radius)
    {
        byte[] bytes = new byte[32];
        Buffer.BlockCopy(new float[] { size.X, size.Y }, 0, bytes, 0, 8);
        Buffer.BlockCopy(new[] { dir.X, dir.Y, radius, 0, 0, 0 }, 0, bytes, 8, 24);
        return bytes;
    }

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        if (!_ready || effectCallbackType != (int)EffectCallbackTypeEnum.PreTransparent) return;

        // Dispose transient RD wrappers (scene buffers here, RDUniforms below) on the render thread.
        // Left to the GC, their finalizers free RIDs off-thread, spamming "free_rid can only be
        // called from the render thread" at random idle moments (Godot issue #104263).
        using var sceneBuffers = renderData.GetRenderSceneBuffers() as RenderSceneBuffersRD;
        if (sceneBuffers is null) return;

        var size = sceneBuffers.GetInternalSize();
        if (size.X == 0 || size.Y == 0) return;

        // The editor also renders this camera at smaller preview resolutions each frame. Recreating
        // the segment texture per size reassigns its published RID every frame, invalidating
        // consumer uniform sets (Godot issue #118292) and spamming "set (1)". Lock onto the largest
        // buffer and skip smaller renders so the RID stays stable.
        if (size.X * size.Y < _maxSize.X * _maxSize.Y) return;
        _maxSize = size;

        EnsureSegmentTexture(size);

        bool blur = ContourSmoothness > 0;
        if (blur) EnsureBlurTemp(size);

        uint xGroups = ((uint)size.X - 1) / 8 + 1;
        uint yGroups = ((uint)size.Y - 1) / 8 + 1;

        byte[] heightPush = Floats(size.X, size.Y, CameraY, NearPlane, FarPlane, HeightMin, HeightMax,
            DepthReversed ? 1.0f : 0.0f);
        byte[] seedPush = Floats(size.X, size.Y, HeightMin, HeightMax, ContourInterval, 0f, 0f, 0f);
        byte[] blurPushH = blur ? BlurPush(size, new(1, 0), ContourSmoothness) : null;
        byte[] blurPushV = blur ? BlurPush(size, new(0, 1), ContourSmoothness) : null;

        uint viewCount = sceneBuffers.GetViewCount();
        for (uint view = 0; view < viewCount; view++)
        {
            var colorImage = sceneBuffers.GetColorLayer(view);
            var depthImage = sceneBuffers.GetDepthLayer(view);

            // RDUniform wrappers are disposed via using at scope end (same render-thread reason).
            using var depthUniform = SamplerUniform(1, _depthSampler, depthImage);
            using var heightColor = ImageUniform(0, colorImage);
            var heightSet = UniformSetCacheRD.GetCache(_heightShader, 0, [heightColor, depthUniform]);

            using var seedColor = ImageUniform(0, colorImage);
            using var seedSegments = ImageUniform(1, _segments);
            var seedSet = UniformSetCacheRD.GetCache(_seedShader, 0, [seedColor, seedSegments]);

            long list = _renderingDevice.ComputeListBegin();

            // Height + mask into the color image.
            _renderingDevice.ComputeListBindComputePipeline(list, _heightPipeline);
            _renderingDevice.ComputeListBindUniformSet(list, heightSet, 0);
            _renderingDevice.ComputeListSetPushConstant(list, heightPush, (uint)heightPush.Length);
            _renderingDevice.ComputeListDispatch(list, xGroups, yGroups, 1);
            _renderingDevice.ComputeListAddBarrier(list);

            // Optional separable box blur of the height buffer, in place: horizontal into the
            // temp target, then vertical back into the color image. Both the seed pass below
            // and the consumer shader then read the smoothed height. Skipped entirely when off,
            // so ContourSmoothness = 0 is byte-for-byte the original pipeline.
            if (blur)
            {
                using var blurHColor = ImageUniform(0, colorImage);
                using var blurHTemp = ImageUniform(1, _blurTemp);
                var blurH = UniformSetCacheRD.GetCache(_blurShader, 0, [blurHColor, blurHTemp]);

                using var blurVTemp = ImageUniform(0, _blurTemp);
                using var blurVColor = ImageUniform(1, colorImage);
                var blurV = UniformSetCacheRD.GetCache(_blurShader, 0, [blurVTemp, blurVColor]);

                _renderingDevice.ComputeListBindComputePipeline(list, _blurPipeline);
                _renderingDevice.ComputeListBindUniformSet(list, blurH, 0);
                _renderingDevice.ComputeListSetPushConstant(list, blurPushH, (uint)blurPushH.Length);
                _renderingDevice.ComputeListDispatch(list, xGroups, yGroups, 1);
                _renderingDevice.ComputeListAddBarrier(list);

                _renderingDevice.ComputeListBindComputePipeline(list, _blurPipeline);
                _renderingDevice.ComputeListBindUniformSet(list, blurV, 0);
                _renderingDevice.ComputeListSetPushConstant(list, blurPushV, (uint)blurPushV.Length);
                _renderingDevice.ComputeListDispatch(list, xGroups, yGroups, 1);
                _renderingDevice.ComputeListAddBarrier(list);
            }

            // Seed pass: per-cell contour segment into the persistent segment texture.
            _renderingDevice.ComputeListBindComputePipeline(list, _seedPipeline);
            _renderingDevice.ComputeListBindUniformSet(list, seedSet, 0);
            _renderingDevice.ComputeListSetPushConstant(list, seedPush, (uint)seedPush.Length);
            _renderingDevice.ComputeListDispatch(list, xGroups, yGroups, 1);

            _renderingDevice.ComputeListEnd();
        }

        _hasProduced = true;
    }
}
