namespace Ch16.CodingAgent;

/// <summary>The tiny repo the coding agent works on: one class and a .git folder it must not touch.</summary>
public static class SampleRepo
{
    public const string Discount = """
        namespace Contoso.Pricing;

        public static class Discount
        {
            public static decimal Apply(decimal price, decimal percent) =>
                price - price * percent / 100m;
        }

        """;

    public static void Create(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        File.WriteAllText(Path.Combine(root, "src", "Discount.cs"), Discount);
        File.WriteAllText(Path.Combine(root, "README.md"), "# Contoso.Pricing\n");
        File.WriteAllText(Path.Combine(root, ".git", "config"), "[core]\n");
    }
}
