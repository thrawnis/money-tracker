using System.Linq;
using System.Reflection;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace MoneyTracker.Tests.Unit;

// Exercises the private JSON import parser on ImportController via reflection.
// Covers this app's own export shape: an array of { account: { name },
// transactions: [...] } groups, including splits and subcategories.
public class JsonImportParserTests
{
    private static IFormFile MakeFile(string content, string name = "test.json")
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", name);
    }

    private static List<object> ParseJson(string content)
    {
        var method = typeof(MoneyTracker.Controllers.ImportController)
            .GetMethod("ParseJson", BindingFlags.NonPublic | BindingFlags.Static)!;
        var result = method.Invoke(null, [MakeFile(content)])!;
        return ((System.Collections.IEnumerable)result).Cast<object>().ToList();
    }

    private static string? Get(object row, string prop) =>
        (string?)row.GetType().GetProperty(prop)!.GetValue(row);

    private static List<object>? GetSplits(object row)
    {
        var value = row.GetType().GetProperty("Splits")!.GetValue(row);
        return value is null ? null : ((System.Collections.IEnumerable)value).Cast<object>().ToList();
    }

    [Fact]
    public void SingleAccountGroup_ParsesAllFields()
    {
        const string json = """
            [
              {
                "account": { "name": "Checking" },
                "transactions": [
                  {
                    "date": "2024-06-01",
                    "payee": "Grocery Store",
                    "category": "Food",
                    "subCategory": "Groceries",
                    "memo": "weekly shop",
                    "amount": -45.67,
                    "status": "Cleared",
                    "checkNumber": "1021"
                  }
                ]
              }
            ]
            """;

        var rows = ParseJson(json);

        rows.Should().HaveCount(1);
        Get(rows[0], "Account").Should().Be("Checking");
        Get(rows[0], "Date").Should().Be("2024-06-01");
        Get(rows[0], "Amount").Should().Be("-45.67");
        Get(rows[0], "Payee").Should().Be("Grocery Store");
        Get(rows[0], "Category").Should().Be("Food");
        Get(rows[0], "SubCategory").Should().Be("Groceries");
        Get(rows[0], "Memo").Should().Be("weekly shop");
        Get(rows[0], "Status").Should().Be("Cleared");
        Get(rows[0], "CheckNumber").Should().Be("1021");
    }

    [Fact]
    public void MultipleAccountGroups_AllParsed()
    {
        const string json = """
            [
              { "account": { "name": "Checking" }, "transactions": [ { "date": "2024-06-01", "amount": -10.00 } ] },
              { "account": { "name": "Savings" },  "transactions": [ { "date": "2024-06-02", "amount": 500.00 } ] }
            ]
            """;

        var rows = ParseJson(json);

        rows.Should().HaveCount(2);
        Get(rows[0], "Account").Should().Be("Checking");
        Get(rows[1], "Account").Should().Be("Savings");
    }

    [Fact]
    public void Splits_ParsedWithColonJoinedSubcategory()
    {
        const string json = """
            [
              {
                "account": { "name": "Checking" },
                "transactions": [
                  {
                    "date": "2024-06-01",
                    "amount": -100.00,
                    "splits": [
                      { "category": "Food", "subCategory": "Groceries", "memo": "food stuff", "amount": -60.00 },
                      { "category": "Household", "memo": "cleaning", "amount": -40.00 }
                    ]
                  }
                ]
              }
            ]
            """;

        var rows = ParseJson(json);

        rows.Should().HaveCount(1);
        var splits = GetSplits(rows[0]);
        splits.Should().HaveCount(2);
        Get(splits![0], "Category").Should().Be("Food:Groceries");
        Get(splits[0], "Amount").Should().Be("-60.00");
        Get(splits[1], "Category").Should().Be("Household");
        Get(splits[1], "Amount").Should().Be("-40.00");
    }

    [Fact]
    public void NoSplits_SplitsIsNull()
    {
        const string json = """
            [
              { "account": { "name": "Checking" }, "transactions": [ { "date": "2024-06-01", "amount": -10.00 } ] }
            ]
            """;

        var rows = ParseJson(json);

        GetSplits(rows[0]).Should().BeNull();
    }

    [Fact]
    public void MissingTransactionsArray_SkipsGroupWithoutThrowing()
    {
        const string json = """
            [
              { "account": { "name": "Checking" } }
            ]
            """;

        var rows = ParseJson(json);

        rows.Should().BeEmpty();
    }

    [Fact]
    public void InvalidRoot_Throws()
    {
        const string json = """{ "not": "an array" }""";

        var act = () => ParseJson(json);

        act.Should().Throw<TargetInvocationException>()
            .WithInnerException<InvalidOperationException>();
    }
}
