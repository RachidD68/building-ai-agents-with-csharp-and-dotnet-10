namespace Ch04.AgentTests;

/// <summary>PASS/FAIL lines and the RESULT tally.</summary>
internal sealed class Harness
{
    private int _passed;
    private int _total;

    public void Section(string title) => Console.WriteLine($"\n-- {title} --");

    public void Expect(string name, bool ok)
    {
        _total++;
        if (ok) _passed++;
        Console.WriteLine($"  {(ok ? "PASS" : "FAIL")}  {name}");
    }

    public int Finish()
    {
        Console.WriteLine($"\nRESULT {_passed}/{_total}");
        return _passed == _total ? 0 : 1;
    }
}
