using System.Globalization;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ExcelDataReader;
using NPOI.SS.UserModel;
using NPOI.XSSF.UserModel;
using MoneyTracker.Auth.Services;
using MoneyTracker.Models;

namespace MoneyTracker.Export;

/// <summary>
/// Produces data exports in QIF, OFX, CSV, XLSX, and JSON formats.
/// All exported values are decrypted plaintext — never ciphertext.
/// </summary>
public class ExportService(IEncryptionService encryption)
{
    public record TransactionExportRow(
        string    AccountName,
        DateOnly  Date,
        string?   CheckNumber,
        string?   Payee,
        string?   Category,
        string?   SubCategory,
        string?   Memo,
        decimal   Amount,
        string    Status);

    // ── QIF ───────────────────────────────────────────────────────────────────

    public string ToQif(IEnumerable<(Account Account, IEnumerable<Transaction> Transactions)> data, string dek)
    {
        var sb = new StringBuilder();

        foreach (var (account, transactions) in data)
        {
            sb.AppendLine($"!Account");
            sb.AppendLine($"N{account.Name}");
            sb.AppendLine($"T{QifAccountType(account.Type)}");
            sb.AppendLine("^");
            sb.AppendLine($"!Type:{QifAccountType(account.Type)}");

            foreach (var tx in transactions.OrderBy(t => t.Date))
            {
                sb.AppendLine($"D{tx.Date:MM/dd/yyyy}");
                sb.AppendLine($"T{tx.Amount:F2}");

                var payee = tx.Payee is not null ? encryption.Decrypt(tx.Payee.NameEncrypted, dek) : null;
                if (!string.IsNullOrEmpty(payee)) sb.AppendLine($"P{payee}");

                var memo = encryption.Decrypt(tx.MemoEncrypted, dek);
                if (!string.IsNullOrEmpty(memo)) sb.AppendLine($"M{memo}");

                var checkNum = encryption.Decrypt(tx.CheckNumberEncrypted, dek);
                if (!string.IsNullOrEmpty(checkNum)) sb.AppendLine($"N{checkNum}");

                if (tx.Category is not null)
                {
                    var catName = encryption.Decrypt(tx.Category.NameEncrypted, dek);
                    sb.AppendLine($"L{catName}");
                }

                sb.AppendLine(tx.Status == TransactionStatus.Cleared ? "CX" :
                              tx.Status == TransactionStatus.Reconciled ? "C*" : "");
                sb.AppendLine("^");
            }
        }

        return sb.ToString();
    }

    // ── OFX ───────────────────────────────────────────────────────────────────

    public string ToOfx(IEnumerable<(Account Account, IEnumerable<Transaction> Transactions)> data, string dek)
    {
        var now = DateTime.UtcNow.ToString("yyyyMMddHHmmss");
        var sb  = new StringBuilder();

        sb.AppendLine("OFXHEADER:100");
        sb.AppendLine("DATA:OFXSGML");
        sb.AppendLine("VERSION:102");
        sb.AppendLine("SECURITY:NONE");
        sb.AppendLine("ENCODING:UTF-8");
        sb.AppendLine("CHARSET:1252");
        sb.AppendLine("COMPRESSION:NONE");
        sb.AppendLine("OLDFILEUID:NONE");
        sb.AppendLine("NEWFILEUID:NONE");
        sb.AppendLine();
        sb.AppendLine("<OFX>");
        sb.AppendLine($"  <SIGNONMSGSRSV1><SONRS><STATUS><CODE>0</CODE><SEVERITY>INFO</SEVERITY></STATUS>");
        sb.AppendLine($"  <DTSERVER>{now}</DTSERVER><LANGUAGE>ENG</LANGUAGE></SONRS></SIGNONMSGSRSV1>");
        sb.AppendLine("  <BANKMSGSRSV1>");

        foreach (var (account, transactions) in data)
        {
            sb.AppendLine("    <STMTTRNRS><TRNUID>1</TRNUID><STATUS><CODE>0</CODE><SEVERITY>INFO</SEVERITY></STATUS>");
            sb.AppendLine("    <STMTRS><CURDEF>USD</CURDEF>");
            sb.AppendLine($"    <BANKACCTFROM><BANKID>0</BANKID><ACCTID>{account.Id}</ACCTID><ACCTTYPE>CHECKING</ACCTTYPE></BANKACCTFROM>");
            sb.AppendLine("    <BANKTRANLIST>");

            foreach (var tx in transactions.OrderBy(t => t.Date))
            {
                var payee = tx.Payee is not null ? encryption.Decrypt(tx.Payee.NameEncrypted, dek) : "";
                var memo  = encryption.Decrypt(tx.MemoEncrypted, dek) ?? "";
                sb.AppendLine("      <STMTTRN>");
                sb.AppendLine($"        <TRNTYPE>{(tx.Amount >= 0 ? "CREDIT" : "DEBIT")}</TRNTYPE>");
                sb.AppendLine($"        <DTPOSTED>{tx.Date:yyyyMMdd}000000</DTPOSTED>");
                sb.AppendLine($"        <TRNAMT>{tx.Amount:F2}</TRNAMT>");
                sb.AppendLine($"        <FITID>{tx.Id}</FITID>");
                if (!string.IsNullOrEmpty(payee)) sb.AppendLine($"        <NAME>{Encode(payee)}</NAME>");
                if (!string.IsNullOrEmpty(memo))  sb.AppendLine($"        <MEMO>{Encode(memo)}</MEMO>");
                sb.AppendLine("      </STMTTRN>");
            }

            sb.AppendLine("    </BANKTRANLIST>");
            sb.AppendLine("    </STMTRS></STMTTRNRS>");
        }

        sb.AppendLine("  </BANKMSGSRSV1>");
        sb.AppendLine("</OFX>");
        return sb.ToString();
    }

    // ── CSV ───────────────────────────────────────────────────────────────────

    public byte[] ToCsv(IEnumerable<TransactionExportRow> rows)
    {
        using var ms     = new MemoryStream();
        using var writer = new StreamWriter(ms, Encoding.UTF8);
        using var csv    = new CsvWriter(writer, new CsvConfiguration(CultureInfo.InvariantCulture));

        csv.WriteRecords(rows);
        writer.Flush();
        return ms.ToArray();
    }

    // ── XLSX ──────────────────────────────────────────────────────────────────

    public byte[] ToXlsx(
        IEnumerable<(Account Account, IEnumerable<Transaction> Transactions)> data,
        IEnumerable<Account> accounts,
        string dek)
    {
        using var ms = new MemoryStream();
        var wb = new XSSFWorkbook();

        // Transactions sheet
        var txSheet = wb.CreateSheet("Transactions");
        var headers = new[] { "Date", "Account", "Payee", "Category", "SubCategory", "Memo", "Amount", "Status", "CheckNumber" };
        var headerRow = txSheet.CreateRow(0);
        for (int i = 0; i < headers.Length; i++)
            headerRow.CreateCell(i).SetCellValue(headers[i]);

        int rowIdx = 1;
        foreach (var (account, transactions) in data)
        {
            foreach (var tx in transactions.OrderByDescending(t => t.Date))
            {
                var row      = txSheet.CreateRow(rowIdx++);
                var payee    = tx.Payee is not null ? encryption.Decrypt(tx.Payee.NameEncrypted, dek) : "";
                var catName  = tx.Category?.ParentId is null
                    ? encryption.Decrypt(tx.Category?.NameEncrypted, dek)
                    : null;
                var subCat   = tx.Category?.ParentId is not null
                    ? encryption.Decrypt(tx.Category?.NameEncrypted, dek)
                    : null;
                var memo     = encryption.Decrypt(tx.MemoEncrypted, dek) ?? "";
                var checkNum = encryption.Decrypt(tx.CheckNumberEncrypted, dek) ?? "";

                row.CreateCell(0).SetCellValue(tx.Date.ToString("yyyy-MM-dd"));
                row.CreateCell(1).SetCellValue(account.Name);
                row.CreateCell(2).SetCellValue(payee ?? "");
                row.CreateCell(3).SetCellValue(catName ?? "");
                row.CreateCell(4).SetCellValue(subCat ?? "");
                row.CreateCell(5).SetCellValue(memo);
                row.CreateCell(6).SetCellValue((double)tx.Amount);
                row.CreateCell(7).SetCellValue(tx.Status.ToString());
                row.CreateCell(8).SetCellValue(checkNum);
            }
        }

        // Accounts sheet
        var acctSheet = wb.CreateSheet("Accounts");
        var acctHeaders = new[] { "Name", "Type", "Opening Balance", "Current Balance" };
        var acctHeaderRow = acctSheet.CreateRow(0);
        for (int i = 0; i < acctHeaders.Length; i++)
            acctHeaderRow.CreateCell(i).SetCellValue(acctHeaders[i]);

        int acctIdx = 1;
        foreach (var acct in accounts)
        {
            var r = acctSheet.CreateRow(acctIdx++);
            r.CreateCell(0).SetCellValue(acct.Name);
            r.CreateCell(1).SetCellValue(acct.Type.ToString());
            r.CreateCell(2).SetCellValue((double)acct.OpeningBalance);
        }

        wb.Write(ms);
        return ms.ToArray();
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    public IEnumerable<TransactionExportRow> ToRows(
        IEnumerable<(Account Account, IEnumerable<Transaction> Transactions)> data,
        string dek)
    {
        foreach (var (account, transactions) in data)
        {
            foreach (var tx in transactions.OrderByDescending(t => t.Date))
            {
                var payee   = tx.Payee is not null ? encryption.Decrypt(tx.Payee.NameEncrypted, dek) : null;
                bool isSub  = tx.Category?.ParentId is not null;
                var catName = isSub ? null : (tx.Category is not null ? encryption.Decrypt(tx.Category.NameEncrypted, dek) : null);
                var subName = isSub ? encryption.Decrypt(tx.Category!.NameEncrypted, dek) : null;
                var memo    = encryption.Decrypt(tx.MemoEncrypted, dek);
                var check   = encryption.Decrypt(tx.CheckNumberEncrypted, dek);

                yield return new TransactionExportRow(
                    account.Name, tx.Date, check, payee, catName, subName, memo, tx.Amount, tx.Status.ToString());
            }
        }
    }

    private static string QifAccountType(AccountType t) => t switch
    {
        AccountType.CreditCard => "CCard",
        AccountType.Cash       => "Cash",
        AccountType.Investment => "Invst",
        _                      => "Bank",
    };

    private static string Encode(string s) =>
        s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
