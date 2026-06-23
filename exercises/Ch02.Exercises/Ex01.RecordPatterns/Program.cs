// ============================================================================
// Exercise 01 — Record Patterns (Beginner)
// Create a Shape hierarchy using records and calculate areas with pattern matching.
// ============================================================================

Console.WriteLine("=== Exercise: Record Patterns — Shape Area Calculator ===\n");

Shape[] shapes =
[
    new Circle(5.0),
    new Rectangle(4.0, 7.0),
    new Triangle(6.0, 3.0),
    new Circle(2.5),
    new Rectangle(10.0, 10.0),
    new Triangle(8.0, 4.5),
];

double totalArea = 0;

foreach (Shape shape in shapes)
{
    double area = CalculateArea(shape);
    totalArea += area;

    string description = DescribeShape(shape);
    Console.WriteLine($"  {description,-35} Area = {area,10:F2}");
}

Console.WriteLine($"\n  {"Total area",-35} = {totalArea,10:F2}");

// ---------------------------------------------------------------
// Bonus: Classify shapes by size using pattern matching
// ---------------------------------------------------------------
Console.WriteLine("\n--- Shape Size Classification ---\n");

foreach (Shape shape in shapes)
{
    string size = ClassifySize(CalculateArea(shape));
    Console.WriteLine($"  {DescribeShape(shape),-35} → {size}");
}

Console.WriteLine("\n=== Exercise complete! ===");

// ---------------------------------------------------------------
// Functions using switch expressions and pattern matching
// ---------------------------------------------------------------

/// <summary>
/// Calculates the area of any <see cref="Shape"/> using a switch expression.
/// </summary>
/// <param name="shape">The shape to calculate the area for.</param>
/// <returns>The computed area.</returns>
static double CalculateArea(Shape shape) => shape switch
{
    Circle(var r) => Math.PI * r * r,
    Rectangle(var w, var h) => w * h,
    Triangle(var b, var h) => 0.5 * b * h,
    _ => throw new ArgumentException($"Unknown shape: {shape.GetType().Name}")
};

/// <summary>
/// Returns a human-readable description of a <see cref="Shape"/>.
/// </summary>
/// <param name="shape">The shape to describe.</param>
/// <returns>A formatted description string.</returns>
static string DescribeShape(Shape shape) => shape switch
{
    Circle(var r) => $"Circle (radius={r})",
    Rectangle(var w, var h) => $"Rectangle ({w} x {h})",
    Triangle(var b, var h) => $"Triangle (base={b}, height={h})",
    _ => "Unknown shape"
};

/// <summary>
/// Classifies an area value into a size category.
/// </summary>
/// <param name="area">The area value to classify.</param>
/// <returns>A size category string.</returns>
static string ClassifySize(double area) => area switch
{
    < 10 => "Small",
    >= 10 and < 50 => "Medium",
    >= 50 and < 100 => "Large",
    _ => "Extra Large"
};

// ---------------------------------------------------------------
// Shape record hierarchy
// ---------------------------------------------------------------

/// <summary>
/// Abstract base record for all shapes.
/// </summary>
abstract record Shape;

/// <summary>
/// Represents a circle defined by its radius.
/// </summary>
/// <param name="Radius">The radius of the circle.</param>
record Circle(double Radius) : Shape;

/// <summary>
/// Represents a rectangle defined by its width and height.
/// </summary>
/// <param name="Width">The width of the rectangle.</param>
/// <param name="Height">The height of the rectangle.</param>
record Rectangle(double Width, double Height) : Shape;

/// <summary>
/// Represents a triangle defined by its base and height.
/// </summary>
/// <param name="Base">The base length of the triangle.</param>
/// <param name="Height">The height of the triangle.</param>
record Triangle(double Base, double Height) : Shape;
