using System;

public class Demo
{
    public void Run(string userInput)
    {
        int unused = 42;                                   // Roslyn: CS0219 unused variable
        var conn = new SqlConnection("Server=x;Password=hunter2"); // not flagged: registry packs miss connection-string secrets (see PHASE-1-PLAN item 4 note)
        var cmd = new SqlCommand("SELECT * FROM t WHERE id = " + userInput, conn); // Semgrep: csharp-sqli SQL injection
        return;
        Console.WriteLine("never runs");                   // Roslyn: CS0162 unreachable code
    }
}
