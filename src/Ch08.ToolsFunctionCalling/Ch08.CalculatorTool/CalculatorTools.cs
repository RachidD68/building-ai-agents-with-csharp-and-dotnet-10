using System.ComponentModel;

namespace Ch08.CalculatorTool;

/// <summary>
/// Provides calculator tool methods for the agent. Each method is static
/// and decorated with <see cref="DescriptionAttribute"/> on both the method
/// and its parameters so the LLM understands when and how to use them.
/// </summary>
public static class CalculatorTools
{
    /// <summary>
    /// Adds two numbers together.
    /// </summary>
    /// <param name="a">The first number.</param>
    /// <param name="b">The second number.</param>
    /// <returns>The sum of the two numbers.</returns>
    [Description("Adds two numbers together and returns the result.")]
    public static double Add(
        [Description("The first number to add")] double a,
        [Description("The second number to add")] double b)
        => a + b;

    /// <summary>
    /// Subtracts the second number from the first.
    /// </summary>
    /// <param name="a">The number to subtract from.</param>
    /// <param name="b">The number to subtract.</param>
    /// <returns>The difference.</returns>
    [Description("Subtracts the second number from the first and returns the result.")]
    public static double Subtract(
        [Description("The number to subtract from")] double a,
        [Description("The number to subtract")] double b)
        => a - b;

    /// <summary>
    /// Multiplies two numbers together.
    /// </summary>
    /// <param name="a">The first number.</param>
    /// <param name="b">The second number.</param>
    /// <returns>The product of the two numbers.</returns>
    [Description("Multiplies two numbers together and returns the result.")]
    public static double Multiply(
        [Description("The first number to multiply")] double a,
        [Description("The second number to multiply")] double b)
        => a * b;

    /// <summary>
    /// Divides the first number by the second.
    /// </summary>
    /// <param name="a">The dividend (number being divided).</param>
    /// <param name="b">The divisor (number to divide by).</param>
    /// <returns>The quotient, or an error message if dividing by zero.</returns>
    [Description("Divides the first number by the second. Returns an error if the divisor is zero.")]
    public static string Divide(
        [Description("The dividend (number being divided)")] double a,
        [Description("The divisor (number to divide by, must not be zero)")] double b)
        => b == 0
            ? "Error: Division by zero is not allowed."
            : (a / b).ToString("G10");

    /// <summary>
    /// Converts a value between common units.
    /// </summary>
    /// <param name="value">The numeric value to convert.</param>
    /// <param name="fromUnit">The source unit (e.g., "USD", "km", "kg", "celsius").</param>
    /// <param name="toUnit">The target unit (e.g., "EUR", "miles", "lbs", "fahrenheit").</param>
    /// <returns>A formatted string describing the conversion result.</returns>
    [Description("Converts a numeric value from one unit to another. Supports currency (USD, EUR, GBP, JPY), distance (km, miles), weight (kg, lbs), and temperature (celsius, fahrenheit).")]
    public static string ConvertUnits(
        [Description("The numeric value to convert")] double value,
        [Description("The source unit (e.g., 'USD', 'km', 'kg', 'celsius')")] string fromUnit,
        [Description("The target unit (e.g., 'EUR', 'miles', 'lbs', 'fahrenheit')")] string toUnit)
    {
        string key = $"{fromUnit.ToUpperInvariant()}->{toUnit.ToUpperInvariant()}";

        var conversions = new Dictionary<string, Func<double, double>>(StringComparer.OrdinalIgnoreCase)
        {
            ["USD->EUR"] = v => v * 0.92,
            ["EUR->USD"] = v => v / 0.92,
            ["USD->GBP"] = v => v * 0.79,
            ["GBP->USD"] = v => v / 0.79,
            ["USD->JPY"] = v => v * 149.50,
            ["JPY->USD"] = v => v / 149.50,
            ["EUR->GBP"] = v => v * 0.86,
            ["GBP->EUR"] = v => v / 0.86,
            ["KM->MILES"] = v => v * 0.621371,
            ["MILES->KM"] = v => v / 0.621371,
            ["KG->LBS"] = v => v * 2.20462,
            ["LBS->KG"] = v => v / 2.20462,
            ["CELSIUS->FAHRENHEIT"] = v => v * 9.0 / 5.0 + 32,
            ["FAHRENHEIT->CELSIUS"] = v => (v - 32) * 5.0 / 9.0,
        };

        if (conversions.TryGetValue(key, out Func<double, double>? converter))
        {
            double result = converter(value);
            return $"{value} {fromUnit} = {result:F2} {toUnit}";
        }

        return $"Conversion from '{fromUnit}' to '{toUnit}' is not supported. " +
               "Supported: USD/EUR/GBP/JPY, km/miles, kg/lbs, celsius/fahrenheit.";
    }
}
