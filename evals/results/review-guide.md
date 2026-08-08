# Worksheet review guide

Generated from evals/cases and the pending worksheet. For each finding, read the code
and set the matching worksheet.json entry (index shown) to "agree" or "disagree".

## Case: clean

### src/Demo/TemperatureConverter.cs
```csharp
  1  namespace Demo;
  2  
  3  public static class TemperatureConverter
  4  {
  5      /// <summary>Converts a Celsius temperature to Fahrenheit.</summary>
  6      public static decimal CelsiusToFahrenheit(decimal celsius)
  7      {
  8          return celsius * 9m / 5m + 32m;
  9      }
 10  
 11      /// <summary>Converts a Fahrenheit temperature to Celsius.</summary>
 12      public static decimal FahrenheitToCelsius(decimal fahrenheit)
 13      {
 14          return (fahrenheit - 32m) * 5m / 9m;
 15      }
 16  }
```

Pending findings:

- **[worksheet index 0]** `src/Demo/TemperatureConverter.cs:3` (docs/llm): New public class `TemperatureConverter` has no XML documentation comment.
- **[worksheet index 1]** `src/Demo/TemperatureConverter.cs:5` (docs/llm): Public method `CelsiusToFahrenheit` documents a summary but omits `<param>` and `<returns>` documentation.
- **[worksheet index 2]** `src/Demo/TemperatureConverter.cs:11` (docs/llm): Public method `FahrenheitToCelsius` documents a summary but omits `<param>` and `<returns>` documentation.

## Case: docs-gaps

### src/Demo/CacheEntry.cs
```csharp
  1  using System;
  2  
  3  namespace Demo;
  4  
  5  public class CacheEntry
  6  {
  7      // Returns null when the key is missing.
  8      public string Get(string key)
  9      {
 10          if (!_store.TryGetValue(key, out var value))
 11          {
 12              throw new KeyNotFoundException(key);
 13          }
 14  
 15          return value;
 16      }
 17  
 18      private readonly Dictionary<string, string> _store = new();
 19  }
```

Pending findings:

- **[worksheet index 3]** `src/Demo/CacheEntry.cs:5` (quality/llm): The class is named CacheEntry but it models an entire key/value store holding many entries, which is misleading.
- **[worksheet index 4]** `src/Demo/CacheEntry.cs:7` (quality/llm): The XML-less comment states the method returns null for a missing key, but the implementation throws KeyNotFoundException, so the documentation contradicts the behaviour.
- **[worksheet index 5]** `src/Demo/CacheEntry.cs:12` (quality/llm): KeyNotFoundException is constructed with the raw key as the exception message, producing an unhelpful error text.
- **[worksheet index 6]** `src/Demo/CacheEntry.cs:18` (quality/llm): The private backing field is declared after the method, which is inconsistent with the common C# convention of declaring fields at the top of the type.
- **[worksheet index 7]** `src/Demo/CacheEntry.cs:5` (docs/llm): New public type CacheEntry has no XML documentation comment.

## Case: mixed-all

### src/Demo/AccountService.cs
```csharp
  1  using System;
  2  using Microsoft.Data.SqlClient;
  3  
  4  namespace Demo;
  5  
  6  public class AccountService
  7  {
  8      private const string ConnectionString = "Server=prod-db;Database=accounts;User Id=svc;Password=hunter2;";
  9  
 10      // Returns null when the account does not exist.
 11      public SqlCommand FindAccount(string accountName)
 12      {
 13          int unused = 42;
 14          var conn = new SqlConnection(ConnectionString);
 15          var cmd = new SqlCommand("SELECT * FROM Accounts WHERE Name = '" + accountName + "'", conn);
 16          return cmd;
 17          Console.WriteLine("never runs");
 18      }
 19  }
```

Pending findings:

- **[worksheet index 8]** `src/Demo/AccountService.cs:8` (quality/llm): Connection string is hardcoded as a compile-time constant, so environments cannot be changed without a rebuild.
- **[worksheet index 9]** `src/Demo/AccountService.cs:10` (quality/llm): The doc comment claims the method returns null when the account does not exist, but the method always returns a non-null SqlCommand and never queries anything, so the comment is misleading.
- **[worksheet index 10]** `src/Demo/AccountService.cs:11` (quality/roslyn): CA1822: Member 'FindAccount' does not access instance data and can be marked as static
- **[worksheet index 11]** `src/Demo/AccountService.cs:14` (quality/llm): The SqlConnection is created but never disposed and ownership is transferred implicitly to the caller via the returned command, which is an easy resource-leak trap.
- **[worksheet index 12]** `src/Demo/AccountService.cs:15` (quality/llm): `SELECT *` couples the code to the table's column layout and returns more data than needed.
- **[worksheet index 13]** `src/Demo/AccountService.cs:6` (docs/llm): New public type AccountService lacks an XML doc comment.
- **[worksheet index 14]** `src/Demo/AccountService.cs:11` (docs/llm): Public method FindAccount has no XML documentation comment describing its parameter, return value, or ownership of the returned command/connection.
- **[worksheet index 15]** `src/Demo/AccountService.cs:11` (docs/llm): The method name FindAccount misleads about behavior: it does not find or return an account, it only constructs a SQL command.

## Case: multi-file

### src/Demo/ReportBuilder.cs
```csharp
  5      public string Build(Report report)
  6      {
  7          var header = FormatHeader(report);
  8          if (report.Kind == "sales")
  9          {
 10              var s = report.Total * 0.0825m;
 11              return header + "\nSales tax: " + s + "\nTotal: " + (report.Total + s);
 12          }
 13          if (report.Kind == "refund")
 14          {
 15              var s = report.Total * 0.0825m;
 16              return header + "\nRefund tax: " + s + "\nTotal: " + (report.Total + s);
 17          }
 18          return header;
 19      }
 20  }
```

### docs/reports.md
```
  1  # Reports
  2  
  3  Sales and refund reports apply an 8 percent sales tax.
  4  Text about reports.
```

Pending findings:

- **[worksheet index 16]** `src/Demo/ReportBuilder.cs:16` (quality/llm): The refund branch adds the tax to the total exactly like the sales branch, which is likely incorrect for a refund and reads as a copy-paste oversight.
- **[worksheet index 17]** `docs/reports.md:3` (quality/llm): Documentation states an 8 percent tax while the code applies 8.25 percent, so the docs and implementation disagree.
- **[worksheet index 18]** `src/Demo/ReportBuilder.cs:8` (quality/llm): The "sales" and "refund" branches duplicate the identical tax calculation and return-format logic, differing only in a label string.
- **[worksheet index 19]** `src/Demo/ReportBuilder.cs:8` (quality/llm): Report kinds are compared against bare string literals ("sales", "refund"), which is fragile and case-sensitive.
- **[worksheet index 20]** `src/Demo/ReportBuilder.cs:8` (docs/llm): The public Build method now has kind-specific tax behavior but carries no XML documentation describing the recognized report kinds or the returned text.

## Case: quality-basics

### src/Demo/InvoicePrinter.cs
```csharp
  1  using System;
  2  
  3  namespace Demo;
  4  
  5  public class InvoicePrinter
  6  {
  7      public decimal PrintInvoice(decimal amount, decimal taxRate)
  8      {
  9          int unused = 42;
 10          var total = amount + amount * taxRate;
 11          Console.WriteLine("Invoice total: " + total);
 12          return total;
 13          Console.WriteLine("never runs");
 14      }
 15  
 16      public decimal PrintReceipt(decimal amount, decimal taxRate)
 17      {
 18          var total = amount + amount * taxRate;
 19          Console.WriteLine("Receipt total: " + total);
 20          return total;
 21      }
 22  }
```

Pending findings:

- **[worksheet index 21]** `src/Demo/InvoicePrinter.cs:7` (quality/roslyn): CA1822: Member 'PrintInvoice' does not access instance data and can be marked as static
- **[worksheet index 22]** `src/Demo/InvoicePrinter.cs:5` (docs/llm): New public type `InvoicePrinter` has no XML documentation comment.
- **[worksheet index 23]** `src/Demo/InvoicePrinter.cs:7` (docs/llm): New public method `PrintInvoice` has no XML documentation comment describing its parameters, return value, or side effect of writing to the console.
- **[worksheet index 24]** `src/Demo/InvoicePrinter.cs:15` (docs/llm): New public method `PrintReceipt` has no XML documentation comment describing its parameters, return value, or console output.
- **[worksheet index 25]** `src/Demo/InvoicePrinter.cs:7` (docs/llm): The name `PrintInvoice` describes only console output, but the method also computes and returns the taxed total, which is misleading without documentation.

## Case: quality-subtle

### src/Demo/ShippingCalculator.cs
```csharp
  1  using System;
  2  
  3  namespace Demo;
  4  
  5  public class ShippingCalculator
  6  {
  7      public decimal Calculate(decimal weight, int zone, bool express, bool insured)
  8      {
  9          if (weight > 0)
 10          {
 11              if (zone > 0)
 12              {
 13                  if (express)
 14                  {
 15                      if (insured)
 16                      {
 17                          return weight * 4.75m + 12.50m + 3.99m;
 18                      }
 19  
 20                      return weight * 4.75m + 12.50m;
 21                  }
 22  
 23                  if (insured)
 24                  {
 25                      return weight * 2.15m + 3.99m;
 26                  }
 27  
 28                  return weight * 2.15m;
 29              }
 30          }
 31  
 32          return 0m;
 33      }
 34  }
```

Pending findings:

- **[worksheet index 26]** `src/Demo/ShippingCalculator.cs:11` (quality/llm): The `zone` parameter is only validated but never used in the fee calculation, so all zones cost the same — likely not the intended behaviour for a shipping calculator.
- **[worksheet index 27]** `src/Demo/ShippingCalculator.cs:7` (quality/roslyn): CA1822: Member 'Calculate' does not access instance data and can be marked as static
- **[worksheet index 28]** `src/Demo/ShippingCalculator.cs:23` (quality/llm): The insurance surcharge and per-weight rate logic is duplicated across four return statements.
- **[worksheet index 29]** `src/Demo/ShippingCalculator.cs:32` (quality/llm): Invalid inputs (non-positive weight or zone) silently return 0m, hiding caller errors instead of signalling them.
- **[worksheet index 30]** `src/Demo/ShippingCalculator.cs:5` (docs/llm): New public type ShippingCalculator has no XML documentation comment.
- **[worksheet index 31]** `src/Demo/ShippingCalculator.cs:7` (docs/llm): New public method Calculate has no XML documentation; its parameters, return value and the silent 0 result for non-positive weight or zone are undocumented.

## Case: security-command

### src/Demo/BackupTool.cs
```csharp
  1  using System;
  2  using System.Diagnostics;
  3  
  4  namespace Demo;
  5  
  6  public class BackupTool
  7  {
  8      public void Run(string directory)
  9      {
 10          var process = Process.Start("/bin/sh", "-c \"tar -czf backup.tar.gz " + directory + "\"");
 11          process?.WaitForExit();
 12      }
 13  }
```

Pending findings:

- **[worksheet index 32]** `src/Demo/BackupTool.cs:10` (quality/llm): The tar command is built by concatenating the raw `directory` argument into a shell string, so any directory containing a space or shell metacharacter produces a wrong or broken command.
- **[worksheet index 33]** `src/Demo/BackupTool.cs:8` (quality/roslyn): CA1822: Member 'Run' does not access instance data and can be marked as static
- **[worksheet index 34]** `src/Demo/BackupTool.cs:10` (quality/llm): The shell path `/bin/sh` and the output file name `backup.tar.gz` are hardcoded, making the class non-portable and impossible to configure or unit test.
- **[worksheet index 35]** `src/Demo/BackupTool.cs:11` (quality/llm): The process exit code is never checked and a null process is silently ignored, so a failed backup looks identical to a successful one to the caller.
- **[worksheet index 36]** `src/Demo/BackupTool.cs:9` (quality/llm): `Run` on `BackupTool` is a vague name that does not convey what the method does or what it returns.
- **[worksheet index 37]** `src/Demo/BackupTool.cs:6` (docs/llm): New public type `BackupTool` has no XML documentation comment.
- **[worksheet index 38]** `src/Demo/BackupTool.cs:8` (docs/llm): New public method `Run` has no XML documentation for its behavior or its `directory` parameter.

## Case: security-injection

### src/Demo/CustomerSearch.cs
```csharp
  1  using System;
  2  using Microsoft.Data.SqlClient;
  3  
  4  namespace Demo;
  5  
  6  public class CustomerSearch
  7  {
  8      private const string ConnectionString = "Server=prod-db;Database=crm;User Id=app;Password=hunter2;";
  9  
 10      public SqlCommand Find(string name)
 11      {
 12          var conn = new SqlConnection(ConnectionString);
 13          var cmd = new SqlCommand("SELECT * FROM Customers WHERE Name = '" + name + "'", conn);
 14          return cmd;
 15      }
 16  }
```

Pending findings:

- **[worksheet index 39]** `src/Demo/CustomerSearch.cs:8` (quality/llm): The database connection string, including credentials, is hardcoded as a compile-time constant instead of being read from configuration, so any environment change requires a recompile.
- **[worksheet index 40]** `src/Demo/CustomerSearch.cs:10` (quality/roslyn): CA1822: Member 'Find' does not access instance data and can be marked as static
- **[worksheet index 41]** `src/Demo/CustomerSearch.cs:13` (quality/llm): The SQL statement is assembled by string concatenation and uses `SELECT *`, which makes the query fragile to schema changes and hard to maintain.
- **[worksheet index 42]** `src/Demo/CustomerSearch.cs:1` (quality/llm): `using System;` appears to be unused by this file.
- **[worksheet index 43]** `src/Demo/CustomerSearch.cs:6` (docs/llm): New public type `CustomerSearch` has no XML documentation comment.
- **[worksheet index 44]** `src/Demo/CustomerSearch.cs:10` (docs/llm): New public method `Find` has no XML documentation; its parameter, return value, and the fact that the caller receives an undisposed command/connection are undocumented.

