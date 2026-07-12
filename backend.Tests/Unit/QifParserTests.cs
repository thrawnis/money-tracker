using System.Linq;
using System.Reflection;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace MoneyTracker.Tests.Unit;

// Exercises the private, loose QIF parser on ImportController via reflection,
// covering the shapes seen in real-world exports: apostrophe-year dates, a
// single !Type section with no embedded account, multi-account !Account
// blocks, transfer markers ("[Account]"), and skipped Invst sections.
public class QifParserTests
{
    private static IFormFile MakeFile(string content, string name = "test.qif")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name);
    }

    private static (List<object> Rows, List<string> Warnings) ParseQif(string content)
    {
        var method = typeof(MoneyTracker.Controllers.ImportController)
            .GetMethod("ParseQif", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = method.Invoke(null, [MakeFile(content)])!;
        // Named tuple element names ("Rows"/"Warnings") are compile-time only —
        // at runtime a ValueTuple exposes public fields Item1/Item2.
        var resultType = result.GetType();
        var rowsField = resultType.GetField("Item1")!;
        var warningsField = resultType.GetField("Item2")!;
        var rows = ((System.Collections.IEnumerable)rowsField.GetValue(result)!).Cast<object>().ToList();
        var warnings = (List<string>)warningsField.GetValue(result)!;
        return (rows, warnings);
    }

    private static string? Get(object row, string prop) =>
        (string?)row.GetType().GetProperty(prop)!.GetValue(row);

    [Fact]
    public void SingleAccountSection_ParsesAllFields()
    {
        const string qif = """
            !Type:Bank
            D6/ 1'24
            T-45.67
            PGrocery Store
            LFood:Groceries
            Nchecking note
            ^
            """;

        var (rows, _) = ParseQif(qif);

        rows.Should().HaveCount(1);
        Get(rows[0], "Date").Should().Be("2024-06-01");
        Get(rows[0], "Amount").Should().Be("-45.67");
        Get(rows[0], "Payee").Should().Be("Grocery Store");
        Get(rows[0], "Category").Should().Be("Food");
        Get(rows[0], "SubCategory").Should().Be("Groceries");
        Get(rows[0], "CheckNumber").Should().Be("checking note");
        Get(rows[0], "Account").Should().BeNull(); // no embedded account — caller supplies one
    }

    [Fact]
    public void MultipleTransactions_AllParsed()
    {
        const string qif = """
            !Type:Bank
            D06/02/2024
            T500.00
            PPaycheck
            LSalary
            ^
            D6/3/24
            T-200.00
            PTransfer to Savings
            L[Savings]
            C*
            ^
            """;

        var (rows, _) = ParseQif(qif);

        rows.Should().HaveCount(2);
        Get(rows[0], "Date").Should().Be("2024-06-02");
        Get(rows[0], "Category").Should().Be("Salary");

        Get(rows[1], "Date").Should().Be("2024-06-03");
        Get(rows[1], "Amount").Should().Be("-200.00");
        // "[Savings]" is a transfer marker, not a real category
        Get(rows[1], "Category").Should().BeNull();
        Get(rows[1], "Status").Should().Be("cleared");
    }

    [Fact]
    public void AccountBlocks_AssignAccountNamePerSection()
    {
        const string qif = """
            !Account
            NChecking
            TBank
            ^
            !Type:Bank
            D6/1/2024
            T-45.67
            PGrocery Store
            LFood:Groceries
            ^
            !Account
            NSavings
            TBank
            ^
            !Type:Bank
            D6/3/2024
            T200.00
            PTransfer from Checking
            L[Checking]
            C*
            ^
            """;

        var (rows, _) = ParseQif(qif);

        rows.Should().HaveCount(2);
        Get(rows[0], "Account").Should().Be("Checking");
        Get(rows[1], "Account").Should().Be("Savings");
    }

    [Fact]
    public void InvstSection_SkippedWithWarning()
    {
        const string qif = """
            !Type:Bank
            D6/1/2024
            T-45.67
            PGrocery Store
            ^
            !Type:Invst
            D6/5/2024
            NBuy
            YFund XYZ
            T50.00
            ^
            """;

        var (rows, warnings) = ParseQif(qif);

        rows.Should().HaveCount(1); // only the Bank-section entry
        warnings.Should().ContainSingle(w => w.Contains("Invst"));
    }

    [Fact]
    public void ParenthesizedAmount_TreatedAsNegative()
    {
        const string qif = """
            !Type:Bank
            D6/1/2024
            T(45.67)
            PSome Fee
            ^
            """;

        var (rows, _) = ParseQif(qif);

        Get(rows[0], "Amount").Should().Be("-45.67");
    }

    [Fact]
    public void MissingTrailingCaret_StillParsesLastRecord()
    {
        const string qif = """
            !Type:Bank
            D6/1/2024
            T-10.00
            PNo Trailing Caret
            """;

        var (rows, _) = ParseQif(qif);

        rows.Should().HaveCount(1);
        Get(rows[0], "Payee").Should().Be("No Trailing Caret");
    }
}
