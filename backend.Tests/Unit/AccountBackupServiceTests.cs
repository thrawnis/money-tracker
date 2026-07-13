using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Moq;
using MoneyTracker.Models;
using MoneyTracker.Services;
using Xunit;

namespace MoneyTracker.Tests.Unit;

public class AccountBackupServiceTests : IDisposable
{
    private readonly string _tempRoot;

    public AccountBackupServiceTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "mt-backup-tests-" + Guid.NewGuid());
        Directory.CreateDirectory(_tempRoot);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempRoot)) Directory.Delete(_tempRoot, recursive: true);
    }

    private AccountBackupService MakeService(int? maxPerAccount = null)
    {
        var settings = new Dictionary<string, string?> { ["Backups:AccountBackupPath"] = "backups" };
        if (maxPerAccount.HasValue) settings["Backups:MaxAccountBackupsPerAccount"] = maxPerAccount.Value.ToString();
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        var env = new Mock<IWebHostEnvironment>();
        env.Setup(e => e.ContentRootPath).Returns(_tempRoot);

        return new AccountBackupService(config, env.Object);
    }

    private static Account MakeAccount(int id = 1, string userId = "user-1", string name = "Checking") => new()
    {
        Id = id,
        UserId = userId,
        Name = name,
        Type = AccountType.Checking,
        OpeningBalance = 100m,
        IsActive = true,
        CreatedAt = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc),
    };

    private string AccountDirPath(int accountId, string userId = "user-1") =>
        Path.Combine(_tempRoot, "backups", userId, accountId.ToString());

    [Fact]
    public async Task CreateBackup_WritesFileUnderUserAndAccountDirectory()
    {
        var svc = MakeService();
        var account = MakeAccount();

        var fileName = await svc.CreateBackupAsync(account, [], note: null);

        File.Exists(Path.Combine(AccountDirPath(account.Id), fileName)).Should().BeTrue();
    }

    [Fact]
    public async Task CreateBackup_DefaultNote_MentionsAccountName()
    {
        var svc = MakeService();
        var account = MakeAccount(name: "My Savings");

        var fileName = await svc.CreateBackupAsync(account, [], note: null);
        var json = await File.ReadAllTextAsync(Path.Combine(AccountDirPath(account.Id), fileName));

        json.Should().Contain("My Savings");
        json.Should().Contain("Automatic backup");
    }

    [Fact]
    public async Task CreateBackup_CustomNote_IsPreserved()
    {
        var svc = MakeService();
        var account = MakeAccount();

        var fileName = await svc.CreateBackupAsync(account, [], note: "Closing this account for good.");
        var json = await File.ReadAllTextAsync(Path.Combine(AccountDirPath(account.Id), fileName));

        json.Should().Contain("Closing this account for good.");
    }

    [Fact]
    public async Task CreateBackup_PrunesOldestBeyondMax_PerAccount()
    {
        var svc = MakeService(maxPerAccount: 3);
        var account = MakeAccount();

        for (int i = 0; i < 5; i++)
        {
            await svc.CreateBackupAsync(account, [], note: $"backup {i}");
            await Task.Delay(1100); // filenames are second-resolution timestamps — ensure distinct ordering
        }

        Directory.GetFiles(AccountDirPath(account.Id)).Should().HaveCount(3);
    }

    [Fact]
    public async Task CreateBackup_DifferentAccounts_PrunedIndependently()
    {
        var svc = MakeService(maxPerAccount: 2);
        var accountA = MakeAccount(id: 1, name: "Checking");
        var accountB = MakeAccount(id: 2, name: "Savings");

        // 3 backups for A, 1 for B — A should prune down to 2, B stays at 1
        await svc.CreateBackupAsync(accountA, [], note: "a1");
        await Task.Delay(1100);
        await svc.CreateBackupAsync(accountA, [], note: "a2");
        await Task.Delay(1100);
        await svc.CreateBackupAsync(accountB, [], note: "b1");
        await Task.Delay(1100);
        await svc.CreateBackupAsync(accountA, [], note: "a3");

        Directory.GetFiles(AccountDirPath(accountA.Id)).Should().HaveCount(2);
        Directory.GetFiles(AccountDirPath(accountB.Id)).Should().HaveCount(1);
    }

    [Fact]
    public async Task ListBackups_ReturnsNewestFirstWithSummaryFields()
    {
        var svc = MakeService();
        var account = MakeAccount(name: "Checking");
        await svc.CreateBackupAsync(account, [], note: "first");

        var list = svc.ListBackups("user-1");

        list.Should().ContainSingle();
        list[0].AccountName.Should().Be("Checking");
        list[0].Note.Should().Be("first");
    }

    [Fact]
    public async Task ListBackups_AcrossMultipleAccounts_ReturnsAll()
    {
        var svc = MakeService();
        await svc.CreateBackupAsync(MakeAccount(id: 1, name: "Checking"), [], note: null);
        await svc.CreateBackupAsync(MakeAccount(id: 2, name: "Savings"), [], note: null);

        svc.ListBackups("user-1").Should().HaveCount(2);
    }

    [Fact]
    public void ListBackups_DifferentUser_SeesNothing()
    {
        var svc = MakeService();
        svc.ListBackups("some-other-user").Should().BeEmpty();
    }

    [Fact]
    public async Task ReadBackup_WrongUser_ReturnsNull()
    {
        var svc = MakeService();
        var fileName = await svc.CreateBackupAsync(MakeAccount(userId: "user-1"), [], note: null);

        svc.ReadBackup("user-2", fileName).Should().BeNull();
    }

    [Theory]
    [InlineData("../evil.json")]
    [InlineData("..\\evil.json")]
    [InlineData("sub/evil.json")]
    [InlineData("sub\\evil.json")]
    public void ReadBackup_PathTraversalAttempt_ReturnsNull(string maliciousName)
    {
        var svc = MakeService();
        svc.ReadBackup("user-1", maliciousName).Should().BeNull();
    }

    [Fact]
    public async Task ReadBackup_ValidFile_ReturnsBytes()
    {
        var svc = MakeService();
        var fileName = await svc.CreateBackupAsync(MakeAccount(), [], note: null);

        var bytes = svc.ReadBackup("user-1", fileName);

        bytes.Should().NotBeNull();
        bytes!.Length.Should().BeGreaterThan(0);
    }
}
