using System.ComponentModel;
using System.Text.Json;
using Microsoft.Agents.AI;

namespace Ch17.AgentSkills;

/// <summary>
/// A class-based skill that bundles instructions, a conversion-table
/// resource, and an in-process convert script into one type. Derives from
/// <see cref="AgentClassSkill{T}"/>; properties annotated with
/// <see cref="AgentSkillResourceAttribute"/> become resources, methods
/// annotated with <see cref="AgentSkillScriptAttribute"/> become scripts.
/// </summary>
internal sealed class UnitConverterSkill : AgentClassSkill<UnitConverterSkill>
{
    public override AgentSkillFrontmatter Frontmatter { get; } = new(
        name: "unit-converter",
        description: "Convert between common units using a multiplication factor. Use when asked to convert miles, kilometres, pounds, or kilograms.");

    protected override string Instructions => """
        Use this skill when the user asks to convert between units.
        1. Review the conversion-table resource to find the correct factor.
        2. Call the convert script, passing the user's value and the factor.
        3. Present the result clearly with both units.
        """;

    [AgentSkillResource("conversion-table")]
    [Description("Lookup table of multiplication factors for common unit conversions.")]
    public string ConversionTable => """
        # Conversion table
        Formula: **result = value × factor**

        | From       | To         | Factor   |
        |------------|------------|----------|
        | miles      | kilometres | 1.60934  |
        | kilometres | miles      | 0.621371 |
        | pounds     | kilograms  | 0.453592 |
        | kilograms  | pounds     | 2.20462  |
        """;

    [AgentSkillScript("convert")]
    [Description("Multiplies a value by a conversion factor and returns the result as JSON.")]
    private static string ConvertUnits(
        [Description("The numeric value to convert.")] double value,
        [Description("The multiplication factor for the conversion.")] double factor)
    {
        double result = Math.Round(value * factor, 4);
        return JsonSerializer.Serialize(new { value, factor, result });
    }
}
