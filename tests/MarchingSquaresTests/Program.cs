using TopographicMap;

int failures = 0;
void Check(bool cond, string name)
{
    Console.WriteLine((cond ? "PASS: " : "FAIL: ") + name);
    if (!cond) failures++;
}

// A 2x2 grid (one cell). Bottom-left low, others high, threshold 0.5 crosses the
// left edge (between bl=0 and tl=1) and the bottom edge (between bl=0 and br=1).
// field index = row * cols + col; row 0 is the top.
// corners: tl=(0,0)=1, tr=(1,0)=1, br=(1,1)=1, bl=(0,1)=0
float[] field = { 1f, 1f, 0f, 1f };
float[] mask = { 1f, 1f, 1f, 1f };
var seg = MarchingSquares.ExtractSegments(field, mask, 2, 2, 0.5f);
Check(seg.Count == 2, "one segment emitted");
// Left edge crossing: between tl(0,0)=1 and bl(0,1)=0 at t where 0.5=1+(0-1)t -> t=0.5 -> (0, 0.5).
// Bottom edge crossing: between bl(0,1)=0 and br(1,1)=1 at t=0.5 -> (0.5, 1).
bool hasLeft = seg.Exists(p => MathF.Abs(p.X - 0f) < 1e-4 && MathF.Abs(p.Y - 0.5f) < 1e-4);
bool hasBottom = seg.Exists(p => MathF.Abs(p.X - 0.5f) < 1e-4 && MathF.Abs(p.Y - 1f) < 1e-4);
Check(hasLeft, "left-edge crossing at (0,0.5)");
Check(hasBottom, "bottom-edge crossing at (0.5,1)");

// A uniform cell (all above) yields no segment.
var none = MarchingSquares.ExtractSegments(new[] { 1f, 1f, 1f, 1f }, mask, 2, 2, 0.5f);
Check(none.Count == 0, "uniform cell yields no segment");

// A masked cell is skipped.
var masked = MarchingSquares.ExtractSegments(field, new[] { 1f, 1f, 0f, 1f }, 2, 2, 0.5f);
Check(masked.Count == 0, "masked cell skipped");

// Chaining: three segments forming one open path A-B-C-D should chain into a
// single 4-point polyline regardless of input order.
var a = new ContourPoint(0f, 0f);
var b = new ContourPoint(0.1f, 0f);
var c = new ContourPoint(0.2f, 0f);
var d = new ContourPoint(0.3f, 0f);
var input = new List<ContourPoint> { c, d, a, b, b, c }; // segments C-D, A-B, B-C
var chains = MarchingSquares.ChainSegments(input);
Check(chains.Count == 1, "three segments chain into one polyline");
Check(chains.Count == 1 && chains[0].Count == 4, "chained polyline has 4 points");

// Build over a simple ramp: field rises left-to-right from 0 to 1 across a 5x2
// grid. With heightMin=0, heightMax=100, interval=25, the interior levels are at
// world 25,50,75 -> normalized 0.25,0.5,0.75. Each should yield one polyline.
float[] ramp =
{
    0.00f, 0.25f, 0.50f, 0.75f, 1.00f,
    0.00f, 0.25f, 0.50f, 0.75f, 1.00f,
};
float[] rampMask = new float[10];
Array.Fill(rampMask, 1f);
var fieldData = ContourField.Build(ramp, rampMask, 5, 2, 0f, 100f, 25f, 5);
Check(fieldData.Polylines.Count == 3, "ramp yields three contour levels");
bool levelsOk = fieldData.Polylines.TrueForAll(p => p.Level is > 0.24f and < 0.76f);
Check(levelsOk, "levels normalized within (0,1)");
bool bboxOk = fieldData.Polylines.TrueForAll(p => p.MaxX >= p.MinX && p.MaxY >= p.MinY);
Check(bboxOk, "bounding boxes valid");

// Simplify: collinear points collapse to the two endpoints, a corner is kept.
var line = new List<ContourPoint>
{
    new(0f, 0f), new(0.25f, 0f), new(0.5f, 0f), new(0.75f, 0f), new(1f, 0f),
};
Check(MarchingSquares.Simplify(line, 0.001f).Count == 2, "collinear polyline simplifies to 2 points");
var corner = new List<ContourPoint> { new(0f, 0f), new(0.5f, 0.5f), new(1f, 0f) };
Check(MarchingSquares.Simplify(corner, 0.001f).Count == 3, "corner point is kept");

Console.WriteLine(failures == 0 ? "ALL PASS" : failures + " FAILED");
return failures == 0 ? 0 : 1;
